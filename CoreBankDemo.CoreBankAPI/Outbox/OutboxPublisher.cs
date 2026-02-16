using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.CoreBankAPI.Outbox;

public interface IOutboxPublisher
{
    Task PublishTransactionCompletedAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        CancellationToken cancellationToken);

    Task PublishTransactionFailedAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        string? errorReason,
        CancellationToken cancellationToken);
}

public class OutboxPublisher(
    IOptions<MessagingOutboxProcessingOptions> options,
    TimeProvider timeProvider) : IOutboxPublisher
{
    private readonly MessagingOutboxProcessingOptions _options = options.Value;
    public async Task PublishTransactionCompletedAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        CancellationToken cancellationToken)
    {
        await CreateOutboxMessageAsync(dbContext, message, "Completed", null, cancellationToken);
    }

    public async Task PublishTransactionFailedAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        string? errorReason,
        CancellationToken cancellationToken)
    {
        await CreateOutboxMessageAsync(dbContext, message, "Failed", errorReason, cancellationToken);
    }

    private async Task CreateOutboxMessageAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        string status,
        string? errorReason,
        CancellationToken cancellationToken)
    {
        var timestamp = timeProvider.GetUtcNow();
        var outboxPartitionCount = _options.PartitionCount;
        var outboxPartitionId = PartitionHelper.GetPartitionId(message.TransactionId!, outboxPartitionCount);

        var eventData = CreateEventData(message, status, errorReason, timestamp);
        var eventType = status == "Completed"
            ? "com.corebank.transaction.completed"
            : "com.corebank.transaction.failed";

        var outboxMessage = new MessagingOutboxMessage
        {
            Id = Guid.NewGuid(),
            PartitionId = outboxPartitionId,
            TransactionId = message.TransactionId!,
            Status = "Pending",
            EventType = eventType,
            EventSource = "https://corebank-api/transactions",
            EventData = JsonSerializer.Serialize(eventData),
            CreatedAt = timestamp.UtcDateTime
        };

        dbContext.MessagingOutboxMessages.Add(outboxMessage);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static Dictionary<string, object> CreateEventData(InboxMessage message, string status, string? errorReason, DateTimeOffset timestamp)
    {
        var eventData = new Dictionary<string, object>
        {
            ["TransactionId"] = message.TransactionId!,
            ["FromAccount"] = message.FromAccount,
            ["ToAccount"] = message.ToAccount,
            ["Amount"] = message.Amount,
            ["Currency"] = message.Currency,
            ["Status"] = status
        };

        if (status == "Completed")
            eventData["ProcessedAt"] = timestamp;
        else
        {
            eventData["Reason"] = errorReason ?? "Unknown error";
            eventData["FailedAt"] = timestamp;
        }

        return eventData;
    }
}
