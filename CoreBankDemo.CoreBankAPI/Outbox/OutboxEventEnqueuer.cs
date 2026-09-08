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

    /// <summary>
    /// Adds a <see cref="Constants.TransactionCancelled"/> row for a cancel
    /// that is about to be saved (spec: instant-rail-cancelled-event). Same
    /// columns as the Failed row with <c>TransactionStatus = Cancelled</c>,
    /// <paramref name="reason"/> in <c>ErrorReason</c> and <c>EventOccurredAt</c>
    /// from the message's <c>ProcessedAt</c>, so the caller must stamp that
    /// first. Returns the added row so a caller whose cancel does not commit
    /// can detach it and keep the context clean for its next save.
    /// </summary>
    Task<MessagingOutboxMessage> EnqueueTransactionCancelledAsync(InboxMessage message, string reason, CancellationToken ct);
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

    public Task<MessagingOutboxMessage> EnqueueTransactionCancelledAsync(InboxMessage message, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var eventOccurredAt = GetEventOccurredAt(message);

        var row = new MessagingOutboxMessage
        {
            Id = Guid.NewGuid(),
            PartitionId = PartitionHelper.GetPartitionId(message.TransactionId, options.Value.PartitionCount),
            IdempotencyKey = message.TransactionId,
            TransactionId = message.TransactionId,
            Status = MessageConstants.Status.Pending,
            EventType = Constants.TransactionCancelled,
            EventSource = "https://corebank-api/transactions",
            AccountNumber = message.FromAccount,
            ToAccount = message.ToAccount,
            Amount = message.Amount,
            Currency = message.Currency,
            TransactionStatus = MessageConstants.Status.Cancelled,
            ErrorReason = reason,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            EventOccurredAt = eventOccurredAt,
            TraceParent = message.TraceParent,
            TraceState = message.TraceState
        };
        dbContext.MessagingOutboxMessages.Add(row);

        return Task.FromResult(row);
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
