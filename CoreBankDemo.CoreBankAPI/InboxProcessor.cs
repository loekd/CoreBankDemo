using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.ServiceDefaults;

namespace CoreBankDemo.CoreBankAPI;

public class InboxProcessor(
    IServiceProvider serviceProvider,
    ILogger<InboxProcessor> logger,
    TimeProvider timeProvider,
    IDistributedLockService lockService)
    : BackgroundService
{
    private readonly ActivitySource _activitySource = new("InboxProcessor");
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);

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
        using var scope = serviceProvider.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var partitionCount = configuration.GetValue<int>("InboxProcessing:PartitionCount");

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
            async ct => await ProcessPartitionMessages(partitionId, ct),
            cancellationToken);
    }

    private async Task ProcessPartitionMessages(int partitionId, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();

        var pendingMessageIds = await GetPendingMessageIdsForPartition(dbContext, partitionId, cancellationToken);

        if (pendingMessageIds.Count == 0)
            return;

        logger.LogInformation("Processing {Count} pending inbox messages in partition {PartitionId}",
            pendingMessageIds.Count, partitionId);

        foreach (var messageId in pendingMessageIds)
        {
            await ProcessMessageIsolated(messageId, cancellationToken);
        }
    }


    private async Task ProcessMessageIsolated(Guid messageId, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();

        // Use execution strategy for transient failures
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            // Use transaction for atomicity of account updates + message status
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                // Reload message in fresh context
                var message = await dbContext.InboxMessages
                    .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);

                if (message == null)
                    return;

                using var activity = _activitySource.StartActivity("ProcessInboxMessage");
                activity?.SetTag("inbox.id", message.Id);
                activity?.SetTag("idempotency.key", message.IdempotencyKey);

                // Load accounts
                var fromAccount = await dbContext.Accounts
                    .FirstOrDefaultAsync(a => a.AccountNumber == message.FromAccount, cancellationToken);

                var toAccount = await dbContext.Accounts
                    .FirstOrDefaultAsync(a => a.AccountNumber == message.ToAccount, cancellationToken);

                // Validate (no DB save yet)
                var validationResult = ValidateTransaction(message, fromAccount, toAccount);

                if (!validationResult.IsValid)
                {
                    // Permanent failure - mark as failed
                    message.Status = "Failed";
                    message.LastError = validationResult.Error;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    logger.LogWarning("Transaction {IdempotencyKey} failed validation: {Error}",
                        message.IdempotencyKey, validationResult.Error);
                    return;
                }

                // Execute transaction (update accounts + message status atomically)
                fromAccount!.Balance -= message.Amount;
                fromAccount.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

                toAccount!.Balance += message.Amount;
                toAccount.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

                var transactionId = Guid.NewGuid().ToString();
                var processedAt = timeProvider.GetUtcNow();
                var response = new TransactionResponse(transactionId, "Completed", processedAt);

                message.TransactionId = transactionId;
                message.Status = "Completed";
                message.ProcessedAt = processedAt.UtcDateTime;
                message.ResponsePayload = JsonSerializer.Serialize(response);

                // Single SaveChanges - all or nothing
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                logger.LogInformation(
                    "Successfully processed inbox message {MessageId} with idempotency key {IdempotencyKey}, transaction {TransactionId}. " +
                    "Transferred {Amount} {Currency} from {FromAccount} to {ToAccount}",
                    message.Id, message.IdempotencyKey, transactionId, message.Amount, message.Currency,
                    message.FromAccount, message.ToAccount);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);

                // Reload message to update retry count (outside transaction)
                var message = await dbContext.InboxMessages
                    .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);

                if (message != null)
                {
                    message.Status = "Pending";
                    message.RetryCount++;
                    message.LastError = ex.Message;
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                logger.LogWarning(ex, "Failed to process inbox message {MessageId}, retry count: {RetryCount}",
                    messageId, message?.RetryCount ?? 0);
            }
        });
    }

    private async Task<List<Guid>> GetPendingMessageIdsForPartition(
        CoreBankDbContext dbContext,
        int partitionId,
        CancellationToken cancellationToken)
    {
        var staleThreshold = timeProvider.GetUtcNow().Subtract(ProcessingTimeout).UtcDateTime;

        return await dbContext.InboxMessages
            .Where(m => m.PartitionId == partitionId &&
                       m.RetryCount < 5 &&
                       (m.Status == "Pending" ||
                        (m.Status == "Processing" && m.ReceivedAt < staleThreshold)))
            .OrderBy(m => m.ReceivedAt)
            .Take(10)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);
    }

    private ValidationResult ValidateTransaction(
        InboxMessage message,
        Account? fromAccount,
        Account? toAccount)
    {
        if (fromAccount == null || !fromAccount.IsActive)
            return ValidationResult.Failure($"Source account {message.FromAccount} not found or inactive");

        if (toAccount == null || !toAccount.IsActive)
            return ValidationResult.Failure($"Destination account {message.ToAccount} not found or inactive");

        if (fromAccount.Balance < message.Amount)
            return ValidationResult.Failure($"Insufficient funds. Available: {fromAccount.Balance}, Required: {message.Amount}");

        return ValidationResult.Success();
    }

    private record ValidationResult(bool IsValid, string? Error)
    {
        public static ValidationResult Success() => new(true, null);
        public static ValidationResult Failure(string error) => new(false, error);
    }
}
