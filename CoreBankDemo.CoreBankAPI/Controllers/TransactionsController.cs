using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace CoreBankDemo.CoreBankAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TransactionsController(
    CoreBankDbContext dbContext,
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

        var validationResult = await ValidateTransactionRequest(request, cancellationToken);
        if (!validationResult.IsValid)
        {
            Activity.Current?.SetTag("outcome", "validation_failed");
            Activity.Current?.SetTag("outcome.errors", string.Join(", ", validationResult.Errors));
            Activity.Current?.SetStatus(ActivityStatusCode.Error, "Validation failed");
            return BadRequest(new { Errors = validationResult.Errors });
        }

        var idempotencyKey = request.TransactionId;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var existing = await dbContext.InboxMessages
                .FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey, cancellationToken);

            if (existing != null)
            {
                Activity.Current?.SetTag("outcome", "duplicate");
                Activity.Current?.SetTag("outcome.existing_status", existing.Status);
                return HandleExistingInboxMessage(existing);
            }

            try
            {
                await ProcessWithInbox(request, cancellationToken);
                break;
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                if (attempt == 3)
                    throw;
            }
        }

        Activity.Current?.SetTag("outcome", "accepted");
        return Accepted($"/api/transactions/{idempotencyKey}", new
        {
            IdempotencyKey = idempotencyKey,
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

    private async Task<ValidationResult> ValidateTransactionRequest(TransactionRequest request, CancellationToken cancellationToken)
    {
        var fromAccount = await dbContext.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == request.FromAccount, cancellationToken);

        var toAccount = await dbContext.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == request.ToAccount, cancellationToken);

        var errors = new List<string>();

        // Validate source account
        if (fromAccount == null)
            errors.Add($"Source account {request.FromAccount} not found");
        else if (!fromAccount.IsActive)
            errors.Add($"Source account {request.FromAccount} is not active");
        else if (fromAccount.Balance < request.Amount)
            errors.Add($"Insufficient funds. Available: {fromAccount.Balance} {fromAccount.Currency}, Required: {request.Amount} {request.Currency}");
        else if (fromAccount.Currency != request.Currency)
            errors.Add($"Currency mismatch. Account currency: {fromAccount.Currency}, Transaction currency: {request.Currency}");

        // Validate destination account
        if (toAccount == null)
            errors.Add($"Destination account {request.ToAccount} not found");
        else if (!toAccount.IsActive)
            errors.Add($"Destination account {request.ToAccount} is not active");

        return new ValidationResult(errors.Count == 0, errors, fromAccount, toAccount);
    }

    private async Task ProcessWithInbox(TransactionRequest request, CancellationToken cancellationToken)
    {
        var idempotencyKey = request.TransactionId;
        var partitionCount = _inboxOptions.PartitionCount;
        var partitionId = PartitionHelper.GetPartitionId(idempotencyKey, partitionCount);

        var inboxMessage = new InboxMessage
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
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

        dbContext.InboxMessages.Add(inboxMessage);
        await dbContext.SaveChangesAsync(cancellationToken);
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

    private record ValidationResult(bool IsValid, List<string> Errors, Account? FromAccount, Account? ToAccount);

    [HttpGet("{idempotencyKey}")]
    public async Task<IActionResult> GetTransactionStatus(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var message = await dbContext.InboxMessages
            .FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey, cancellationToken);

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
