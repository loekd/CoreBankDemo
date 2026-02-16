using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.PaymentsAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController(
    PaymentsDbContext dbContext,
    IOptions<OutboxProcessingOptions> outboxOptions,
    TimeProvider timeProvider) : ControllerBase
{
    private readonly OutboxProcessingOptions _outboxOptions = outboxOptions.Value;

    [HttpPost]
    public async Task<IActionResult> ProcessPayment([FromBody] PaymentRequest request, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
            return BadRequest(new { Errors = GetModelErrors() });

        var paymentId = Guid.NewGuid().ToString();
        var messageId = Guid.NewGuid().ToString();

        // Retry loop to handle unique constraint violations (concurrency)
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            // Check for duplicate message
            var existing = await dbContext.OutboxMessages
                .FirstOrDefaultAsync(m => m.MessageId == messageId, cancellationToken);

            if (existing != null)
            {
                return Accepted($"/api/payments/{existing.TransactionId}",
                    CreatePendingResponse(existing.TransactionId, request));
            }

            try
            {
                await StoreInOutbox(request, paymentId, messageId, cancellationToken);
                break; // Success - exit retry loop
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqliteEx &&
                                               sqliteEx.SqliteErrorCode == 19)
            {
                // UNIQUE constraint violation - another instance inserted this message
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                if (attempt == 3)
                    throw;
            }
        }

        return Accepted($"/api/payments/{paymentId}", CreatePendingResponse(paymentId, request));
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
            Status = "Pending"
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
