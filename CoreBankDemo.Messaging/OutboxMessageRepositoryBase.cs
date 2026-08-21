using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.Messaging;

/// <summary>
/// Base repository for outbox message stores (story 2.2). Concrete stores
/// implement <see cref="OutboxMessages"/> over their own <c>DbSet</c> and, in
/// their <c>DbContext.OnModelCreating</c>, call
/// <see cref="MessageRepositoryBase{TMessage,TDbContext}.ConfigureDedupeIndex"/>
/// to declare whether the store dedupes on the idempotency key alone (command
/// store) or a composite event identity (event store — AD-4). Claiming,
/// retry/poison handling, and processor-facing queries land in story 2.3.
/// </summary>
public abstract class OutboxMessageRepositoryBase<TMessage, TDbContext> : MessageRepositoryBase<TMessage, TDbContext>
    where TMessage : class, IOutboxMessage
    where TDbContext : DbContext
{
    protected OutboxMessageRepositoryBase(TDbContext dbContext, TimeProvider timeProvider)
        : base(dbContext, timeProvider)
    {
    }

    /// <summary>The outbox message table.</summary>
    protected abstract DbSet<TMessage> OutboxMessages { get; }

    protected override DbSet<TMessage> Messages => OutboxMessages;
}
