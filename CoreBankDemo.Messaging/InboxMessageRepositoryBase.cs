using CoreBankDemo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.Messaging;

/// <summary>
/// Base repository for inbox message stores (story 2.2). Concrete stores
/// implement <see cref="InboxMessages"/> over their own <c>DbSet</c> and, in
/// their <c>DbContext.OnModelCreating</c>, call
/// <see cref="MessageRepositoryBase{TMessage,TDbContext}.ConfigureDedupeIndex"/>
/// to declare whether the store dedupes on the idempotency key alone (command
/// store) or a composite event identity (event store — AD-4). Claiming and
/// retry/poison handling land in story 2.3; implements
/// <see cref="IInboxMessageStore{TMessage}"/> (story 2.5) — the narrow port
/// <see cref="InboxProcessorBase{TMessage}"/> depends on — via the members
/// already inherited from <see cref="MessageRepositoryBase{TMessage,TDbContext}"/>.
/// </summary>
public abstract class InboxMessageRepositoryBase<TMessage, TDbContext>
    : MessageRepositoryBase<TMessage, TDbContext>, IInboxMessageStore<TMessage>
    where TMessage : class, IInboxMessage
    where TDbContext : DbContext
{
    protected InboxMessageRepositoryBase(TDbContext dbContext, TimeProvider timeProvider, BusinessMetrics businessMetrics)
        : base(dbContext, timeProvider, businessMetrics)
    {
    }

    /// <summary>The inbox message table.</summary>
    protected abstract DbSet<TMessage> InboxMessages { get; }

    protected override DbSet<TMessage> Messages => InboxMessages;

    /// <summary>Every inbox store reports <see cref="BusinessMetrics.StoreKind.Inbox"/> (story 6.5) — fixed here, never overridden by a leaf repository.</summary>
    protected override BusinessMetrics.StoreKind StoreKind => BusinessMetrics.StoreKind.Inbox;

    /// <inheritdoc/>
    protected override IQueryable<TMessage> GetClaimableMessagesQuery(int partitionId, DateTime staleThreshold) =>
        InboxMessages
            .Where(m =>
                m.PartitionId == partitionId &&
                m.RetryCount < MessageConstants.Defaults.MaxRetryCount &&
                (m.Status == MessageConstants.Status.Pending ||
                 (m.Status == MessageConstants.Status.Processing && m.ReceivedAt < staleThreshold)))
            .OrderBy(m => m.ReceivedAt)
            .ThenBy(m => m.Id);

    /// <inheritdoc/>
    protected override void SetOrderingTimestamp(TMessage message, DateTime claimedAt) =>
        message.ReceivedAt = claimedAt;
}
