using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

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
        if (!ModelState.IsValid)
            return BadRequest(new { Errors = GetModelErrors() });

        var validationResult = await ValidateTransactionRequest(request, cancellationToken);
        if (!validationResult.IsValid)
            return BadRequest(new { Errors = validationResult.Errors });

        var idempotencyKey = request.TransactionId;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            // Check for duplicate request
            var existing = await dbContext.InboxMessages
                .FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey, cancellationToken);

            if (existing != null)
                return HandleExistingInboxMessage(existing);
            try
            {
                await ProcessWithInbox(request, cancellationToken);
                break;
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                // Another instance has inserted this transaction in the inbox
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                if (attempt == 3)
                {
                    throw;
                }
            }
        }
        
        return Accepted($"/api/transactions/{idempotencyKey}", new
        {
            IdempotencyKey = idempotencyKey,
            Status = "Pending",
            Message = "Transaction accepted for processing"
        });
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
            TransactionId = request.TransactionId
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
