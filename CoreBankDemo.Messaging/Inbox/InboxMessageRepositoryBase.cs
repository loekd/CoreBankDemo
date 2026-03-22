using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreBankDemo.Messaging.Inbox;

/// <summary>
/// Base repository for inbox pattern with idempotency, partitioning, and retry logic.
/// </summary>
public abstract class InboxMessageRepositoryBase<TMessage, TDbContext>
    where TMessage : class, IInboxMessage
    where TDbContext : DbContext
{
    protected readonly TDbContext DbContext;
    protected readonly TimeProvider TimeProvider;
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);

    protected InboxMessageRepositoryBase(TDbContext dbContext, TimeProvider timeProvider)
    {
        DbContext = dbContext;
        TimeProvider = timeProvider;
    }

    /// <summary>
    /// Override to provide access to the DbSet for inbox messages.
    /// </summary>
    protected abstract DbSet<TMessage> InboxMessages { get; }

    public virtual async Task<TMessage?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        return await InboxMessages
            .FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey, cancellationToken);
    }

    public virtual async Task<bool> StoreIfNewAsync(
        TMessage message,
        CancellationToken cancellationToken)
    {
        var exists = await InboxMessages
            .AnyAsync(m => m.IdempotencyKey == message.IdempotencyKey, cancellationToken);

        if (exists)
            return false;

        InboxMessages.Add(message);
        try
        {
            await DbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Concurrent insert race — already stored by another instance
            return false;
        }
    }

    public virtual async Task<TMessage?> LoadMessageAsync(
        Guid messageId,
        CancellationToken cancellationToken)
    {
        return await InboxMessages
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
    }

    public virtual async Task<List<Guid>> GetPendingMessageIdsForPartitionAsync(
        int partitionId,
        CancellationToken cancellationToken)
    {
        var staleThreshold = TimeProvider.GetUtcNow().Subtract(ProcessingTimeout).UtcDateTime;

        return await InboxMessages
            .Where(m => m.PartitionId == partitionId &&
                       m.RetryCount < 5 &&
                       (m.Status == "Pending" ||
                        (m.Status == "Processing" && m.ReceivedAt < staleThreshold)))
            .OrderBy(m => m.ReceivedAt)
            .Take(10)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);
    }

    public virtual async Task MarkAsProcessingAsync(
        TMessage message,
        CancellationToken cancellationToken)
    {
        await InboxMessages
            .Where(m => m.Id == message.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "Processing"), cancellationToken);
    }

    public virtual async Task MarkMessageAsFailedWithRetryAsync(
        Guid messageId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await InboxMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, "Pending")
                .SetProperty(m => m.RetryCount, m => m.RetryCount + 1)
                .SetProperty(m => m.LastError, errorMessage), cancellationToken);
    }

    /// <summary>
    /// Executes work and marks the message as completed atomically.
    /// </summary>
    public virtual async Task ExecuteInTransactionAsync(
        TMessage message,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        var strategy = DbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await DbContext.Database.BeginTransactionAsync(cancellationToken);

            await work(cancellationToken);

            var processedAt = TimeProvider.GetUtcNow().UtcDateTime;
            await InboxMessages
                .Where(m => m.Id == message.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, "Completed")
                    .SetProperty(m => m.ProcessedAt, processedAt), cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        });
    }

    /// <summary>
    /// Gets recent inbox messages for monitoring purposes.
    /// </summary>
    public virtual async Task<List<TMessage>> GetRecentMessagesAsync(
        int count = 50,
        CancellationToken cancellationToken = default)
    {
        return await InboxMessages
            .OrderByDescending(m => m.ReceivedAt)
            .Take(count)
            .ToListAsync(cancellationToken);
    }
}
