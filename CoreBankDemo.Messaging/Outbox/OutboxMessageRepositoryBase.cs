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
        message.Status = Status.Completed;
        message.ProcessedAt = TimeProvider.GetUtcNow().UtcDateTime;
        await DbContext.SaveChangesAsync(cancellationToken);
    }

    public virtual async Task MarkAsFailedWithRetryAsync(
        TMessage message,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        message.Status = Status.Pending;
        message.RetryCount++;
        message.LastError = errorMessage;
        await DbContext.SaveChangesAsync(cancellationToken);
    }
}
