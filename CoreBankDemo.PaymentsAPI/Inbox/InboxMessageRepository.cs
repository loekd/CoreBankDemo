using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreBankDemo.PaymentsAPI.Inbox;

public interface IInboxMessageRepository
{
    Task<bool> StoreIfNewAsync(
        InboxMessage message,
        CancellationToken cancellationToken);

    Task<InboxMessage?> LoadMessageAsync(
        Guid messageId,
        CancellationToken cancellationToken);

    Task<List<Guid>> GetPendingMessageIdsForPartitionAsync(
        int partitionId,
        CancellationToken cancellationToken);

    Task MarkAsProcessingAsync(
        InboxMessage message,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes <paramref name="work"/> and marks the message as completed atomically
    /// </summary>
    Task ExecuteInTransactionAsync(
        InboxMessage message,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken);

    Task MarkMessageAsFailedWithRetryAsync(
        Guid messageId,
        string errorMessage,
        CancellationToken cancellationToken);
}

public class InboxMessageRepository(PaymentsDbContext dbContext, TimeProvider timeProvider) : IInboxMessageRepository
{
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);

    public async Task<bool> StoreIfNewAsync(
        InboxMessage message,
        CancellationToken cancellationToken)
    {
        var exists = await dbContext.InboxMessages
            .AnyAsync(m => m.IdempotencyKey == message.IdempotencyKey, cancellationToken);

        if (exists)
            return false;

        dbContext.InboxMessages.Add(message);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Concurrent insert race — already stored by another instance
            return false;
        }
    }

    public async Task<InboxMessage?> LoadMessageAsync(
        Guid messageId,
        CancellationToken cancellationToken)
    {
        return await dbContext.InboxMessages
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
    }

    public async Task<List<Guid>> GetPendingMessageIdsForPartitionAsync(
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

    public async Task MarkAsProcessingAsync(
        InboxMessage message,
        CancellationToken cancellationToken)
    {
        await dbContext.InboxMessages
            .Where(m => m.Id == message.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "Processing"), cancellationToken);
    }

    public async Task ExecuteInTransactionAsync(
        InboxMessage message,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            await work(cancellationToken);

            var processedAt = timeProvider.GetUtcNow().UtcDateTime;
            await dbContext.InboxMessages
                .Where(m => m.Id == message.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, "Completed")
                    .SetProperty(m => m.ProcessedAt, processedAt), cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        });
    }

    public async Task MarkMessageAsFailedWithRetryAsync(
        Guid messageId,
        string errorMessage,
        CancellationToken cancellationToken)
    {

        await dbContext.InboxMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, "Pending")
                .SetProperty(m => m.RetryCount, m => m.RetryCount + 1)
                .SetProperty(m => m.LastError, errorMessage), cancellationToken);
    }
}
