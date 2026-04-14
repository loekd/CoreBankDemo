using Microsoft.EntityFrameworkCore;
using Npgsql;
using static CoreBankDemo.Messaging.MessageConstants;

namespace CoreBankDemo.Messaging.Outbox;

/// <summary>
/// Base repository for outbox pattern with deduplication and retry logic.
/// </summary>
public abstract class OutboxMessageRepositoryBase<TMessage, TDbContext>
    where TMessage : class, IOutboxMessage
    where TDbContext : DbContext
{
    protected readonly TDbContext DbContext;
    protected readonly TimeProvider TimeProvider;

    protected OutboxMessageRepositoryBase(TDbContext dbContext, TimeProvider timeProvider)
    {
        DbContext = dbContext;
        TimeProvider = timeProvider;
    }

    /// <summary>
    /// Override to provide access to the DbSet for outbox messages.
    /// </summary>
    protected abstract DbSet<TMessage> OutboxMessages { get; }

    public virtual async Task<TMessage?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        return await OutboxMessages
            .FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey, cancellationToken);
    }

    public virtual async Task<bool> StoreIfNewAsync(
        TMessage message,
        CancellationToken cancellationToken)
    {
        OutboxMessages.Add(message);
        try
        {
            await DbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Concurrent insert race — already stored by another instance
            DbContext.Entry(message).State = EntityState.Detached;
            return false;
        }
    }

    public virtual async Task<TMessage?> LoadMessageAsync(
        Guid messageId,
        CancellationToken cancellationToken)
    {
        return await OutboxMessages
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
    }

    public virtual async Task<List<Guid>> GetPendingMessageIdsForPartitionAsync(
        int partitionId,
        CancellationToken cancellationToken)
    {
        var staleThreshold = TimeProvider.GetUtcNow().Subtract(Defaults.ProcessingTimeout).UtcDateTime;

        return await OutboxMessages
            .Where(m => m.PartitionId == partitionId &&
                       m.RetryCount < Defaults.MaxRetryCount &&
                       (m.Status == Status.Pending ||
                        (m.Status == Status.Processing && m.CreatedAt < staleThreshold)))
            .OrderBy(m => m.CreatedAt)
            .Take(Defaults.BatchSize)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);
    }

    public virtual async Task MarkAsCompletedAsync(
        TMessage message,
        CancellationToken cancellationToken)
    {
        var processedAt = TimeProvider.GetUtcNow().UtcDateTime;
        await OutboxMessages
            .Where(m => m.Id == message.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, Status.Completed)
                .SetProperty(m => m.ProcessedAt, processedAt), cancellationToken);
    }

    public virtual async Task MarkAsFailedWithRetryAsync(
        TMessage message,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await OutboxMessages
            .Where(m => m.Id == message.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, Status.Pending)
                .SetProperty(m => m.RetryCount, m => m.RetryCount + 1)
                .SetProperty(m => m.LastError, errorMessage), cancellationToken);
    }

    /// <summary>
    /// Gets recent outbox messages for monitoring purposes.
    /// </summary>
    public virtual async Task<List<TMessage>> GetRecentMessagesAsync(
        int count = 50,
        CancellationToken cancellationToken = default)
    {
        return await OutboxMessages
            .OrderByDescending(m => m.CreatedAt)
            .Take(count)
            .ToListAsync(cancellationToken);
    }
}
