using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreBankDemo.PaymentsAPI.Controllers;

[ApiController]
public class TransactionEventsController(
    IInboxMessageRepository inboxRepository,
    PaymentsDbContext dbContext,
    IOptions<InboxProcessingOptions> inboxOptions,
    TimeProvider timeProvider,
    ILogger<TransactionEventsController> logger) : ControllerBase
{
    private readonly InboxProcessingOptions _inboxOptions = inboxOptions.Value;

    [HttpPost("events/transactions/completed")]
    public async Task<IActionResult> TransactionCompleted(
        [FromBody] TransactionCompletedEvent e,
        CancellationToken cancellationToken = default)
    {
        var idempotencyKey = $"{e.TransactionId}-{nameof(TransactionCompletedEvent)}";
        await StoreInInbox(idempotencyKey, nameof(TransactionCompletedEvent), JsonSerializer.Serialize(e), cancellationToken);
        return Ok();
    }

    [HttpPost("events/transactions/failed")]
    public async Task<IActionResult> TransactionFailed(
        [FromBody] TransactionFailedEvent e,
        CancellationToken cancellationToken = default)
    {
        var idempotencyKey = $"{e.TransactionId}-{nameof(TransactionFailedEvent)}";
        await StoreInInbox(idempotencyKey, nameof(TransactionFailedEvent), JsonSerializer.Serialize(e), cancellationToken);
        return Ok();
    }

    [HttpPost("events/transactions/balance-updated")]
    public async Task<IActionResult> BalanceUpdated(
        [FromBody] BalanceUpdatedEvent e,
        CancellationToken cancellationToken = default)
    {
        var idempotencyKey = $"{e.TransactionId}-{nameof(BalanceUpdatedEvent)}-{e.AccountNumber}";
        await StoreInInbox(idempotencyKey, nameof(BalanceUpdatedEvent), JsonSerializer.Serialize(e), cancellationToken);
        return Ok();
    }

    [HttpPost("events/transactions/unknown")]
    public IActionResult Unknown()
    {
        logger.LogWarning("Received unknown cloud event type, ignoring");
        return Ok();
    }

    private async Task StoreInInbox(string idempotencyKey, string eventType, string eventPayload, CancellationToken cancellationToken)
    {
        var partitionId = PartitionHelper.GetPartitionId(idempotencyKey, _inboxOptions.PartitionCount);

        var message = new InboxMessage
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            PartitionId = partitionId,
            EventType = eventType,
            EventPayload = eventPayload,
            ReceivedAt = timeProvider.GetUtcNow().UtcDateTime,
            Status = "Pending",
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString
        };

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var isNew = await inboxRepository.StoreIfNewAsync(dbContext, message, cancellationToken);
                if (!isNew)
                {
                    logger.LogInformation("Duplicate event ignored: {IdempotencyKey}", idempotencyKey);
                }
                return;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx &&
                                               pgEx.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                if (attempt < 3)
                {
                    // Transient race between check and insert — retry
                    dbContext.ChangeTracker.Clear();
                    continue;
                }

                // After retries, treat as duplicate
                logger.LogInformation("Duplicate event detected via constraint: {IdempotencyKey}", idempotencyKey);
            }
        }
    }
}
