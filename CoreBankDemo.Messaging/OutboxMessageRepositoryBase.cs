using CoreBankDemo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.Messaging;

/// <summary>
/// Base repository for outbox message stores (story 2.2). Concrete stores
/// implement <see cref="OutboxMessages"/> over their own <c>DbSet</c> and, in
/// their <c>DbContext.OnModelCreating</c>, call
/// <see cref="MessageRepositoryBase{TMessage,TDbContext}.ConfigureDedupeIndex"/>
/// to declare whether the store dedupes on the idempotency key alone (command
/// store) or a composite event identity (event store — AD-4). Claiming and
/// retry/poison handling land in story 2.3; implements
/// <see cref="IOutboxMessageStore{TMessage}"/> (story 2.4) — the narrow port
/// <see cref="OutboxProcessorBase{TMessage}"/> depends on — via the members
/// already inherited from <see cref="MessageRepositoryBase{TMessage,TDbContext}"/>.
/// </summary>
public abstract class OutboxMessageRepositoryBase<TMessage, TDbContext>
    : MessageRepositoryBase<TMessage, TDbContext>, IOutboxMessageStore<TMessage>
    where TMessage : class, IOutboxMessage
    where TDbContext : DbContext
{
    protected OutboxMessageRepositoryBase(TDbContext dbContext, TimeProvider timeProvider, BusinessMetrics businessMetrics)
        : base(dbContext, timeProvider, businessMetrics)
    {
    }

    /// <summary>The outbox message table.</summary>
    protected abstract DbSet<TMessage> OutboxMessages { get; }

    protected override DbSet<TMessage> Messages => OutboxMessages;

    /// <summary>Every outbox store reports <see cref="BusinessMetrics.StoreKind.Outbox"/> (story 6.5) — fixed here, never overridden by a leaf repository.</summary>
    protected override BusinessMetrics.StoreKind StoreKind => BusinessMetrics.StoreKind.Outbox;

    /// <inheritdoc/>
    protected override IQueryable<TMessage> GetClaimableMessagesQuery(int partitionId, DateTime staleThreshold) =>
        OutboxMessages
            .Where(m =>
                m.PartitionId == partitionId &&
                m.RetryCount < MessageConstants.Defaults.MaxRetryCount &&
                (m.Status == MessageConstants.Status.Pending ||
                 (m.Status == MessageConstants.Status.Processing && m.CreatedAt < staleThreshold)))
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id);

    /// <inheritdoc/>
    protected override void SetOrderingTimestamp(TMessage message, DateTime claimedAt) =>
        message.CreatedAt = claimedAt;
}
