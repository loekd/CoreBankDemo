using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace CoreBankDemo.CoreBankAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TransactionsController(
    IInboxMessageRepository inboxRepository,
    IAccountRepository accountRepository,
    IOptions<InboxProcessingOptions> inboxOptions,
    TimeProvider timeProvider)
    : ControllerBase
{
    private readonly InboxProcessingOptions _inboxOptions = inboxOptions.Value;

    [HttpPost("process")]
    public async Task<IActionResult> ProcessTransaction([FromBody] TransactionRequest request, CancellationToken cancellationToken = default)
    {
        // Enrich the current span (propagated from the Payments outbox via Dapr) with transaction details
        EnrichCurrentActivity(request);

        if (!ModelState.IsValid)
        {
            var errors = GetModelErrors();
            Activity.Current?.SetTag("outcome", "invalid_request");
            Activity.Current?.SetTag("outcome.errors", string.Join(", ", errors));
            Activity.Current?.SetStatus(ActivityStatusCode.Error, "Invalid request");
            return BadRequest(new { Errors = errors });
        }

        var validationResult = await accountRepository.ValidateTransactionRequestAsync(
            request.FromAccount, request.ToAccount, request.Amount, request.Currency, cancellationToken);

        if (!validationResult.IsValid)
        {
            Activity.Current?.SetTag("outcome", "validation_failed");
            Activity.Current?.SetTag("outcome.errors", string.Join(", ", validationResult.Errors));
            Activity.Current?.SetStatus(ActivityStatusCode.Error, "Validation failed");
            return BadRequest(new { Errors = validationResult.Errors });
        }

        var existing = await inboxRepository.FindByIdempotencyKeyAsync(request.TransactionId, cancellationToken);
        if (existing != null)
        {
            Activity.Current?.SetTag("outcome", "duplicate");
            Activity.Current?.SetTag("outcome.existing_status", existing.Status);
            return HandleExistingInboxMessage(existing);
        }

        var isNew = await inboxRepository.StoreIfNewAsync(BuildInboxMessage(request), cancellationToken);
        if (!isNew)
        {
            // Lost a concurrent race — load and return the winner's record
            existing = await inboxRepository.FindByIdempotencyKeyAsync(request.TransactionId, cancellationToken);
            Activity.Current?.SetTag("outcome", "duplicate");
            return existing != null
                ? HandleExistingInboxMessage(existing)
                : StatusCode(500, new { Errors = new[] { "Failed to store or retrieve transaction" } });
        }

        Activity.Current?.SetTag("outcome", "accepted");
        return Accepted($"/api/transactions/{request.TransactionId}", new
        {
            IdempotencyKey = request.TransactionId,
            Status = "Pending",
            Message = "Transaction accepted for processing"
        });
    }

    private static void EnrichCurrentActivity(TransactionRequest request)
    {
        var activity = Activity.Current;
        if (activity == null) return;
        activity.SetTag("transaction.id", request.TransactionId);
        activity.SetTag("transaction.from_account", request.FromAccount);
        activity.SetTag("transaction.to_account", request.ToAccount);
        activity.SetTag("transaction.amount", request.Amount);
        activity.SetTag("transaction.currency", request.Currency);
    }

    private InboxMessage BuildInboxMessage(TransactionRequest request)
    {
        var partitionId = PartitionHelper.GetPartitionId(request.TransactionId, _inboxOptions.PartitionCount);

        return new InboxMessage
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = request.TransactionId,
            PartitionId = partitionId,
            FromAccount = request.FromAccount,
            ToAccount = request.ToAccount,
            Amount = request.Amount,
            Currency = request.Currency,
            ReceivedAt = timeProvider.GetUtcNow().UtcDateTime,
            Status = "Pending",
            TransactionId = request.TransactionId,
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString
        };
    }

    private IActionResult HandleExistingInboxMessage(InboxMessage existing)
    {
        switch (existing.Status)
        {
            case "Completed" when !string.IsNullOrEmpty(existing.ResponsePayload):
            {
                var cachedResponse = JsonSerializer.Deserialize<TransactionResponse>(existing.ResponsePayload);
                return Ok(cachedResponse);
            }
            case "Pending" or "Processing":
                return Accepted($"/api/transactions/{existing.IdempotencyKey}", new
                {
                    IdempotencyKey = existing.IdempotencyKey,
                    Status = existing.Status,
                    Message = "Transaction is being processed"
                });
            case "Failed":
                return BadRequest(new { Errors = new[] { existing.LastError ?? "Transaction failed" } });
            default:
                return StatusCode(500, new { Errors = new[] { "Unknown transaction status" } });
        }
    }

    private List<string> GetModelErrors()
    {
        return ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .ToList();
    }

    [HttpGet("{idempotencyKey}")]
    public async Task<IActionResult> GetTransactionStatus(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var message = await inboxRepository.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken);

        if (message == null)
            return NotFound(new { Errors = new[] { "Transaction not found" } });

        if (message.Status == "Completed" && !string.IsNullOrEmpty(message.ResponsePayload))
        {
            var response = JsonSerializer.Deserialize<TransactionResponse>(message.ResponsePayload);
            return Ok(response);
        }

        return Ok(new
        {
            IdempotencyKey = message.IdempotencyKey,
            Status = message.Status,
            ReceivedAt = message.ReceivedAt,
            ProcessedAt = message.ProcessedAt
        });
    }
}
