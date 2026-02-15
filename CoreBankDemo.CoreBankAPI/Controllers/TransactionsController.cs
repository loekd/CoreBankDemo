using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Models;

namespace CoreBankDemo.CoreBankAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TransactionsController(
    CoreBankDbContext dbContext,
    IConfiguration configuration,
    TimeProvider timeProvider)
    : ControllerBase
{
    [HttpPost("process")]
    public async Task<IActionResult> ProcessTransaction([FromBody] TransactionRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(new { Errors = GetModelErrors() });

        var validationResult = await ValidateTransactionRequest(request);
        if (!validationResult.IsValid)
            return BadRequest(new { Errors = validationResult.Errors });

        var idempotencyKey = request.IdempotencyKey;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            // Check for duplicate request
            var existing = await dbContext.InboxMessages
                .FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey);

            if (existing != null)
                return HandleExistingInboxMessage(existing);
            try
            {
                await ProcessWithInbox(request);
                break;
            }
            catch (DbUpdateException)
            {
                // Another instance has inserted this transaction in the inbox
                await Task.Delay(TimeSpan.FromSeconds(1));
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

    private async Task<ValidationResult> ValidateTransactionRequest(TransactionRequest request)
    {
        var fromAccount = await dbContext.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == request.FromAccount);

        var toAccount = await dbContext.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == request.ToAccount);

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

    private async Task ProcessWithInbox(TransactionRequest request)
    {
        var idempotencyKey = request.IdempotencyKey;
        var partitionCount = configuration.GetValue<int>("InboxProcessing:PartitionCount");
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
            Status = "Pending"
        };

        dbContext.InboxMessages.Add(inboxMessage);
        await dbContext.SaveChangesAsync();
    }

    private IActionResult HandleExistingInboxMessage(InboxMessage existing)
    {
        if (existing.Status == "Completed" && !string.IsNullOrEmpty(existing.ResponsePayload))
        {
            var cachedResponse = JsonSerializer.Deserialize<TransactionResponse>(existing.ResponsePayload);
            return Ok(cachedResponse);
        }

        if (existing.Status is "Pending" or "Processing")
        {
            return Accepted($"/api/transactions/{existing.IdempotencyKey}", new
            {
                IdempotencyKey = existing.IdempotencyKey,
                Status = existing.Status,
                Message = "Transaction is being processed"
            });
        }

        if (existing.Status is "Failed")
            return BadRequest(new { Errors = new[] { existing.LastError ?? "Transaction failed" } });

        return StatusCode(500, new { Errors = new[] { "Unknown transaction status" } });
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
    public async Task<IActionResult> GetTransactionStatus(string idempotencyKey)
    {
        var message = await dbContext.InboxMessages
            .FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey);

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
