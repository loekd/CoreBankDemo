using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
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
        string transactionStatus,
        string? errorReason,
        CancellationToken cancellationToken)
    {
        var timestamp = timeProvider.GetUtcNow();
        var outboxPartitionCount = _options.PartitionCount;
        var outboxPartitionId = PartitionHelper.GetPartitionId(message.TransactionId!, outboxPartitionCount);

        var eventType = transactionStatus == "Completed"
            ? Constants.TransactionCompleted
            : Constants.TransactionFailed;

        var outboxMessage = new MessagingOutboxMessage
        {
            Id = Guid.NewGuid(),
            PartitionId = outboxPartitionId,
            TransactionId = message.TransactionId!,
            Status = "Pending",
            EventType = eventType,
            EventSource = "https://corebank-api/transactions",
            FromAccount = message.FromAccount,
            ToAccount = message.ToAccount,
            Amount = message.Amount,
            Currency = message.Currency,
            TransactionStatus = transactionStatus,
            ErrorReason = errorReason,
            CreatedAt = timestamp.UtcDateTime
        };

        dbContext.MessagingOutboxMessages.Add(outboxMessage);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
