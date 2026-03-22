using Microsoft.EntityFrameworkCore;
using Npgsql;

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
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);

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
        var staleThreshold = TimeProvider.GetUtcNow().Subtract(ProcessingTimeout).UtcDateTime;

        return await OutboxMessages
            .Where(m => m.PartitionId == partitionId &&
                       m.RetryCount < 5 &&
                       (m.Status == "Pending" ||
                        (m.Status == "Processing" && m.CreatedAt < staleThreshold)))
            .OrderBy(m => m.CreatedAt)
            .Take(10)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);
    }

    public virtual async Task MarkAsCompletedAsync(
        TMessage message,
        CancellationToken cancellationToken)
    {
        message.Status = "Completed";
        message.ProcessedAt = TimeProvider.GetUtcNow().UtcDateTime;
        await DbContext.SaveChangesAsync(cancellationToken);
    }

    public virtual async Task MarkAsFailedWithRetryAsync(
        TMessage message,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        message.Status = "Pending";
        message.RetryCount++;
        message.LastError = errorMessage;
        await DbContext.SaveChangesAsync(cancellationToken);
    }
}
