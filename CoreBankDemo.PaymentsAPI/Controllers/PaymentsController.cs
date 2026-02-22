using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace CoreBankDemo.PaymentsAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController(
    PaymentsDbContext dbContext,
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

        var paymentId = Guid.NewGuid().ToString();
        var messageId = Guid.NewGuid().ToString();
        activity?.SetTag("payment.id", paymentId);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var existing = await dbContext.OutboxMessages
                .FirstOrDefaultAsync(m => m.MessageId == messageId, cancellationToken);

            if (existing != null)
            {
                activity?.SetTag("outcome", "duplicate");
                return Accepted($"/api/payments/{existing.TransactionId}",
                    CreatePendingResponse(existing.TransactionId, request));
            }

            try
            {
                await StoreInOutbox(request, paymentId, messageId, cancellationToken);
                break;
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqliteEx &&
                                               sqliteEx.SqliteErrorCode == 19)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                if (attempt == 3)
                    throw;
            }
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

    private async Task StoreInOutbox(PaymentRequest request, string paymentId, string messageId, CancellationToken cancellationToken)
    {
        var partitionCount = _outboxOptions.PartitionCount;
        var partitionId = PartitionHelper.GetPartitionId(messageId, partitionCount);

        var outboxMessage = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            PartitionId = partitionId,
            TransactionId = paymentId,
            FromAccount = request.FromAccount,
            ToAccount = request.ToAccount,
            Amount = request.Amount,
            Currency = request.Currency,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            Status = "Pending",
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString
        };

        dbContext.OutboxMessages.Add(outboxMessage);
        await dbContext.SaveChangesAsync(cancellationToken);
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
