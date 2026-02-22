using System.Diagnostics;
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

    Task PublishBalanceUpdatedAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        string accountNumber,
        decimal newBalance,
        CancellationToken cancellationToken);
}

public class OutboxPublisher(
    IOptions<MessagingOutboxProcessingOptions> options,
    TimeProvider timeProvider) : IOutboxPublisher
{
    private readonly MessagingOutboxProcessingOptions _options = options.Value;

    public Task PublishTransactionCompletedAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        CancellationToken cancellationToken)
    {
        return CreateOutboxMessageAsync(dbContext, message, "Completed", null, cancellationToken);
    }

    public Task PublishTransactionFailedAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        string? errorReason,
        CancellationToken cancellationToken)
    {
        return CreateOutboxMessageAsync(dbContext, message, "Failed", errorReason, cancellationToken);
    }

    public Task PublishBalanceUpdatedAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        string accountNumber,
        decimal newBalance,
        CancellationToken cancellationToken)
    {
        var timestamp = timeProvider.GetUtcNow();
        var outboxPartitionId = PartitionHelper.GetPartitionId(accountNumber, _options.PartitionCount);
        var delta = accountNumber == message.FromAccount ? -message.Amount : message.Amount;

        var outboxMessage = new MessagingOutboxMessage
        {
            Id = Guid.NewGuid(),
            PartitionId = outboxPartitionId,
            TransactionId = message.TransactionId!,
            Status = "Pending",
            EventType = Constants.BalanceUpdated,
            EventSource = "https://corebank-api/accounts",
            FromAccount = accountNumber,
            ToAccount = accountNumber,
            Amount = delta,
            NewBalance = newBalance,
            Currency = message.Currency,
            TransactionStatus = "Completed",
            CreatedAt = timestamp.UtcDateTime,
            TraceParent = message.TraceParent ?? Activity.Current?.Id,
            TraceState = message.TraceState ?? Activity.Current?.TraceStateString
        };

        dbContext.MessagingOutboxMessages.Add(outboxMessage);
        return Task.CompletedTask;
    }

    private Task CreateOutboxMessageAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        string transactionStatus,
        string? errorReason,
        CancellationToken cancellationToken)
    {
        var timestamp = timeProvider.GetUtcNow();
        var outboxPartitionId = PartitionHelper.GetPartitionId(message.TransactionId!, _options.PartitionCount);

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
            CreatedAt = timestamp.UtcDateTime,
            TraceParent = message.TraceParent ?? Activity.Current?.Id,
            TraceState = message.TraceState ?? Activity.Current?.TraceStateString
        };

        dbContext.MessagingOutboxMessages.Add(outboxMessage);
        return Task.CompletedTask;
    }
}
