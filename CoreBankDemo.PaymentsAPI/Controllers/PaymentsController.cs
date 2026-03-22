using Microsoft.AspNetCore.Mvc;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using CoreBankDemo.Messaging;
using static CoreBankDemo.Messaging.MessageConstants;

namespace CoreBankDemo.PaymentsAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController(
    IOutboxRepository outboxRepository,
    IOptions<OutboxProcessingOptions> outboxOptions,
    TimeProvider timeProvider) : ControllerBase
{
    private static readonly ActivitySource ActivitySource = new(nameof(PaymentsController));
    private readonly OutboxProcessingOptions _outboxOptions = outboxOptions.Value;

    [HttpPost]
    public async Task<IActionResult> ProcessPayment([FromBody] PaymentRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = StartPaymentActivity(request);

        if (!ModelState.IsValid)
        {
            var errors = GetModelErrors();
            activity?.SetTag("outcome", "invalid_request");
            activity?.SetTag("outcome.errors", string.Join(", ", errors));
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid request");
            return BadRequest(new { Errors = errors });
        }

        // Use client-provided idempotency key if present, otherwise generate new one
        var idempotencyKeyFromHeader = Request.Headers["Idempotency-Key"].FirstOrDefault();
        var paymentId = idempotencyKeyFromHeader ?? Guid.NewGuid().ToString();
        activity?.SetTag("payment.id", paymentId);
        activity?.SetTag("payment.idempotency_key_provided", idempotencyKeyFromHeader != null);

        var outboxMessage = BuildOutboxMessage(request, paymentId);
        var isNew = await outboxRepository.StoreIfNewAsync(outboxMessage, cancellationToken);
        if (!isNew)
        {
            var existing = await outboxRepository.FindByIdempotencyKeyAsync(paymentId, cancellationToken);
            activity?.SetTag("outcome", "duplicate");
            return Accepted($"/api/payments/{existing?.TransactionId ?? paymentId}",
                CreatePendingResponse(existing?.TransactionId ?? paymentId, request));
        }

        activity?.SetTag("outcome", "accepted");
        return Accepted($"/api/payments/{paymentId}", CreatePendingResponse(paymentId, request));
    }

    private static Activity? StartPaymentActivity(PaymentRequest request)
    {
        var activity = ActivitySource.StartActivity("Payment.Received", ActivityKind.Server);
        activity?.SetTag("payment.from_account", request.FromAccount);
        activity?.SetTag("payment.to_account", request.ToAccount);
        activity?.SetTag("payment.amount", request.Amount);
        activity?.SetTag("payment.currency", request.Currency);
        return activity;
    }

    private OutboxMessage BuildOutboxMessage(PaymentRequest request, string paymentId)
    {
        var partitionId = PartitionHelper.GetPartitionId(paymentId, _outboxOptions.PartitionCount);

        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = paymentId,
            PartitionId = partitionId,
            TransactionId = paymentId,
            FromAccount = request.FromAccount,
            ToAccount = request.ToAccount,
            Amount = request.Amount,
            Currency = request.Currency,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            Status = Status.Pending,
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString
        };
    }

    private List<string> GetModelErrors()
    {
        return ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .ToList();
    }

    private PaymentResponse CreatePendingResponse(string paymentId, PaymentRequest request)
    {
        return new PaymentResponse(
            paymentId,
            "pending",
            "Pending",
            request.Amount,
            request.Currency,
            timeProvider.GetUtcNow()
        );
    }
}
