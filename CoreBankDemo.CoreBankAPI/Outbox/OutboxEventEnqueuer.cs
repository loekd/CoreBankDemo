using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.CoreBankAPI.Outbox;

internal interface IOutboxEventEnqueuer
{
    Task EnqueueTransactionCompletedAsync(InboxMessage message, CancellationToken ct);
    Task EnqueueTransactionFailedAsync(InboxMessage message, string? errorReason, CancellationToken ct);
    Task EnqueueBalanceUpdatedAsync(InboxMessage message, string accountNumber, decimal delta, decimal newBalance, CancellationToken ct);
}

internal sealed class OutboxEventEnqueuer(
    CoreBankDbContext dbContext,
    IOptions<MessagingOutboxProcessingOptions> options,
    TimeProvider timeProvider) : IOutboxEventEnqueuer
{
    public Task EnqueueTransactionCompletedAsync(InboxMessage message, CancellationToken ct)
    {
        var eventOccurredAt = GetEventOccurredAt(message);

        dbContext.MessagingOutboxMessages.Add(new MessagingOutboxMessage
        {
            Id = Guid.NewGuid(),
            PartitionId = PartitionHelper.GetPartitionId(message.TransactionId, options.Value.PartitionCount),
            IdempotencyKey = message.TransactionId,
            TransactionId = message.TransactionId,
            Status = MessageConstants.Status.Pending,
            EventType = Constants.TransactionCompleted,
            EventSource = "https://corebank-api/transactions",
            AccountNumber = message.FromAccount,
            ToAccount = message.ToAccount,
            Amount = message.Amount,
            Currency = message.Currency,
            TransactionStatus = MessageConstants.Status.Completed,
            ErrorReason = null,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            EventOccurredAt = eventOccurredAt,
            TraceParent = message.TraceParent,
            TraceState = message.TraceState
        });

        return Task.CompletedTask;
    }

    public Task EnqueueTransactionFailedAsync(InboxMessage message, string? errorReason, CancellationToken ct)
    {
        var eventOccurredAt = GetEventOccurredAt(message);

        dbContext.MessagingOutboxMessages.Add(new MessagingOutboxMessage
        {
            Id = Guid.NewGuid(),
            PartitionId = PartitionHelper.GetPartitionId(message.TransactionId, options.Value.PartitionCount),
            IdempotencyKey = message.TransactionId,
            TransactionId = message.TransactionId,
            Status = MessageConstants.Status.Pending,
            EventType = Constants.TransactionFailed,
            EventSource = "https://corebank-api/transactions",
            AccountNumber = message.FromAccount,
            ToAccount = message.ToAccount,
            Amount = message.Amount,
            Currency = message.Currency,
            TransactionStatus = MessageConstants.Status.Failed,
            ErrorReason = errorReason,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            EventOccurredAt = eventOccurredAt,
            TraceParent = message.TraceParent,
            TraceState = message.TraceState
        });

        return Task.CompletedTask;
    }

    public Task EnqueueBalanceUpdatedAsync(InboxMessage message, string accountNumber, decimal delta, decimal newBalance, CancellationToken ct)
    {
        var eventOccurredAt = GetEventOccurredAt(message);

        dbContext.MessagingOutboxMessages.Add(new MessagingOutboxMessage
        {
            Id = Guid.NewGuid(),
            PartitionId = PartitionHelper.GetPartitionId(accountNumber, options.Value.PartitionCount),
            IdempotencyKey = message.TransactionId,
            TransactionId = message.TransactionId,
            Status = MessageConstants.Status.Pending,
            EventType = Constants.BalanceUpdated,
            EventSource = "https://corebank-api/accounts",
            AccountNumber = accountNumber,
            ToAccount = accountNumber,
            Amount = delta,
            NewBalance = newBalance,
            Currency = message.Currency,
            TransactionStatus = MessageConstants.Status.Completed,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            EventOccurredAt = eventOccurredAt,
            TraceParent = message.TraceParent,
            TraceState = message.TraceState
        });

        return Task.CompletedTask;
    }

    private static DateTime GetEventOccurredAt(InboxMessage message) =>
        message.ProcessedAt
        ?? throw new InvalidOperationException(
            $"Inbox message {message.Id} must have ProcessedAt stamped before domain events are enqueued.");
}
