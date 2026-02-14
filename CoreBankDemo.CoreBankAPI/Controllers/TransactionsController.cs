using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Models;

namespace CoreBankDemo.CoreBankAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TransactionsController : ControllerBase
{
    private readonly CoreBankDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;

    public TransactionsController(
        CoreBankDbContext dbContext,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _timeProvider = timeProvider;
    }

    [HttpPost("process")]
    public async Task<IActionResult> ProcessTransaction([FromBody] TransactionRequest request)
    {
        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .ToList();
            return BadRequest(new { Errors = errors });
        }

        var useInbox = _configuration.GetValue<bool>("Features:UseInbox");
        var idempotencyKey = request.IdempotencyKey;

        // Validate accounts exist and have sufficient funds
        var fromAccount = await _dbContext.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == request.FromAccount);

        var toAccount = await _dbContext.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == request.ToAccount);

        var validationErrors = new List<string>();

        if (fromAccount == null)
            validationErrors.Add($"Source account {request.FromAccount} not found");
        else if (!fromAccount.IsActive)
            validationErrors.Add($"Source account {request.FromAccount} is not active");
        else if (fromAccount.Balance < request.Amount)
            validationErrors.Add($"Insufficient funds. Available: {fromAccount.Balance} {fromAccount.Currency}, Required: {request.Amount} {request.Currency}");
        else if (fromAccount.Currency != request.Currency)
            validationErrors.Add($"Currency mismatch. Account currency: {fromAccount.Currency}, Transaction currency: {request.Currency}");

        if (toAccount == null)
            validationErrors.Add($"Destination account {request.ToAccount} not found");
        else if (!toAccount.IsActive)
            validationErrors.Add($"Destination account {request.ToAccount} is not active");

        if (validationErrors.Any())
            return BadRequest(new { Errors = validationErrors });

        if (useInbox && !string.IsNullOrEmpty(idempotencyKey))
        {
            // Check if already received (idempotency check)
            var existing = await _dbContext.InboxMessages
                .FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey);

            if (existing != null)
            {
                // Already received - return cached response based on status
                if (existing.Status == "Completed" && !string.IsNullOrEmpty(existing.ResponsePayload))
                {
                    var cachedResponse = JsonSerializer.Deserialize<TransactionResponse>(existing.ResponsePayload);
                    return Ok(cachedResponse);
                }
                else if (existing.Status == "Pending" || existing.Status == "Processing")
                {
                    // Still processing
                    return Accepted($"/api/transactions/{existing.IdempotencyKey}", new
                    {
                        IdempotencyKey = existing.IdempotencyKey,
                        Status = existing.Status,
                        Message = "Transaction is being processed"
                    });
                }
                else if (existing.Status == "Failed")
                {
                    return BadRequest(new { Errors = new[] { existing.LastError ?? "Transaction failed" } });
                }
            }

            // Validation passed - store in inbox for processing
            var inboxMessage = new InboxMessage
            {
                Id = Guid.NewGuid(),
                IdempotencyKey = idempotencyKey,
                FromAccount = request.FromAccount,
                ToAccount = request.ToAccount,
                Amount = request.Amount,
                Currency = request.Currency,
                ReceivedAt = _timeProvider.GetUtcNow().UtcDateTime,
                Status = "Pending"
            };

            _dbContext.InboxMessages.Add(inboxMessage);
            await _dbContext.SaveChangesAsync();

            return Accepted($"/api/transactions/{idempotencyKey}", new
            {
                IdempotencyKey = idempotencyKey,
                Status = "Pending",
                Message = "Transaction accepted for processing"
            });
        }

        // Direct processing (when inbox is disabled)
        // Deduct from source account
        fromAccount!.Balance -= request.Amount;
        fromAccount.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        // Credit to destination account
        toAccount!.Balance += request.Amount;
        toAccount.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        await _dbContext.SaveChangesAsync();

        var response = new TransactionResponse(
            Guid.NewGuid().ToString(),
            "Completed",
            _timeProvider.GetUtcNow()
        );

        return Ok(response);
    }

    [HttpGet("{idempotencyKey}")]
    public async Task<IActionResult> GetTransactionStatus(string idempotencyKey)
    {
        var message = await _dbContext.InboxMessages
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
