using System.Diagnostics;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.CoreBankAPI.Inbox;

public class InboxProcessor(
    IServiceProvider serviceProvider,
    ILogger<InboxProcessor> logger,
    IDistributedLockService lockService,
    TransactionValidator transactionValidator,
    IOptions<InboxProcessingOptions> options)
    : BackgroundService
{
    private readonly ActivitySource _activitySource = new("InboxProcessor");
    private readonly InboxProcessingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Inbox Processor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPartitions(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing inbox partitions");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessPartitions(CancellationToken cancellationToken)
    {
        var partitionCount = _options.PartitionCount;
        // Process all partitions in parallel for maximum throughput
        logger.LogInformation("Processing {PartitionCount} partitions", partitionCount);
        var tasks = Enumerable.Range(0, partitionCount)
            .Select(partitionId => ProcessPartition(partitionId, cancellationToken))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    private async Task ProcessPartition(int partitionId, CancellationToken cancellationToken)
    {
        var lockName = $"inbox-partition-{partitionId}";

        await lockService.ExecuteWithLockAsync(
            lockName,
            _options.LockExpirySeconds,
            async ct => await ProcessPartitionMessages(partitionId, ct),
            cancellationToken);
    }

    private async Task ProcessPartitionMessages(int partitionId, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IInboxMessageRepository>();

        var pendingMessageIds = await repository.GetPendingMessageIdsForPartitionAsync(
            partitionId, cancellationToken);

        if (pendingMessageIds.Count == 0)
            return;

        logger.LogInformation("Processing {Count} pending inbox messages in partition {PartitionId}",
            pendingMessageIds.Count, partitionId);

        foreach (var messageId in pendingMessageIds)
        {
            await ProcessMessageIsolatedAsync(messageId, cancellationToken);
        }
    }

    private async Task ProcessMessageIsolatedAsync(Guid messageId, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<IInboxMessageRepository>();
        var executor = scope.ServiceProvider.GetRequiredService<ITransactionExecutor>();
        var publisher = scope.ServiceProvider.GetRequiredService<IOutboxPublisher>();
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                var message = await repository.LoadMessageAsync(messageId, cancellationToken);
                if (message == null)
                    return;

                using var activity = CreateActivity(message);

                var (fromAccount, toAccount) = await executor.LoadAccountsAsync(dbContext, message, cancellationToken);
                var validationResult = transactionValidator.ValidateTransaction(message, fromAccount, toAccount);

                if (!validationResult.IsValid)
                {
                    await HandleFailedTransactionAsync(dbContext, message, validationResult, executor, publisher, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return;
                }

                await ExecuteSuccessfulTransactionAsync(dbContext, message, fromAccount!, toAccount!, executor, publisher, cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                LogTransactionSuccess(message);
            }
            catch (Exception ex)
            {
                await HandleTransactionErrorAsync(transaction, repository, messageId, ex, cancellationToken);
            }
        });
    }

    private Activity? CreateActivity(InboxMessage message)
    {
        ActivityContext parentContext;
        var hasParent = !string.IsNullOrWhiteSpace(message.TraceParent)
                        && ActivityContext.TryParse(message.TraceParent, message.TraceState, out parentContext);

        var activity = hasParent
            ? _activitySource.StartActivity("ProcessInboxMessage", ActivityKind.Consumer, parentContext)
            : _activitySource.StartActivity("ProcessInboxMessage", ActivityKind.Consumer);

        activity?.SetTag("inbox.id", message.Id);
        activity?.SetTag("idempotency.key", message.IdempotencyKey);
        activity?.SetTag("queue_duration_ms", (long)(DateTime.UtcNow - message.ReceivedAt).TotalMilliseconds);
        return activity;
    }

    private async Task HandleFailedTransactionAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        ValidationResult validationResult,
        ITransactionExecutor executor,
        IOutboxPublisher publisher,
        CancellationToken cancellationToken)
    {
        executor.PrepareFailedTransaction(message, validationResult.Error);
        await publisher.PublishTransactionFailedAsync(dbContext, message, validationResult.Error, cancellationToken);
        await executor.SaveAsync(dbContext, cancellationToken);

        logger.LogWarning("Transaction {IdempotencyKey} failed validation: {Error}",
            message.IdempotencyKey, validationResult.Error);
    }

    private async Task ExecuteSuccessfulTransactionAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        Account fromAccount,
        Account toAccount,
        ITransactionExecutor executor,
        IOutboxPublisher publisher,
        CancellationToken cancellationToken)
    {
        var (newFromBalance, newToBalance) = executor.ApplySuccessfulTransaction(message, fromAccount, toAccount);

        await publisher.PublishTransactionCompletedAsync(dbContext, message, cancellationToken);
        await publisher.PublishBalanceUpdatedAsync(dbContext, message, message.FromAccount, newFromBalance, cancellationToken);
        await publisher.PublishBalanceUpdatedAsync(dbContext, message, message.ToAccount, newToBalance, cancellationToken);

        await executor.SaveAsync(dbContext, cancellationToken);
    }

    private async Task HandleTransactionErrorAsync(
        IDbContextTransaction transaction,
        IInboxMessageRepository repository,
        Guid messageId,
        Exception ex,
        CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        await repository.MarkMessageAsFailedWithRetryAsync(messageId, ex.Message, cancellationToken);

        logger.LogWarning(ex, "Failed to process inbox message {MessageId}",
            messageId);
    }

    private void LogTransactionSuccess(InboxMessage message)
    {
        logger.LogInformation(
            "Successfully processed inbox message {MessageId} with idempotency key {IdempotencyKey}, transaction {TransactionId}. " +
            "Transferred {Amount} {Currency} from {FromAccount} to {ToAccount}",
            message.Id, message.IdempotencyKey, message.TransactionId, message.Amount, message.Currency,
            message.FromAccount, message.ToAccount);
    }
}
