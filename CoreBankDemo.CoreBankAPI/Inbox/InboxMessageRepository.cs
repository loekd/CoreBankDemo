using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI.Inbox;

/// <summary>
/// Narrow port <see cref="TransactionIntakeHandler"/> depends on (story 4.4).
/// <see cref="StoreIfNewAsync"/> is satisfied automatically by
/// <see cref="InboxMessageRepositoryBase{TMessage,TDbContext}"/>'s inherited,
/// already race-safe implementation (AD-4) — this port exists to give intake
/// a narrow, mockable surface (no database) rather than exposing the kernel
/// base class directly, plus the new <see cref="FindByIdempotencyKeyAsync"/>
/// lookup the kernel base does not offer (it only exposes
/// <c>FindByIdAsync(Guid)</c>, keyed on the row's internal id).
/// </summary>
internal interface IInboxMessageRepository
{
    Task<bool> StoreIfNewAsync(InboxMessage message, CancellationToken cancellationToken);

    Task<InboxMessage?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken);
}

/// <summary>
/// Dual-role store (spec-4-4): the same class is both this story's intake
/// repository (<see cref="IInboxMessageRepository"/>) and, unmodified, the
/// future kernel inbox processor's store (story 4.6, via the inherited
/// <see cref="InboxMessageRepositoryBase{TMessage,TDbContext}"/> members) —
/// exactly mirroring legacy's dual role for this type.
/// </summary>
internal sealed class InboxMessageRepository
    : InboxMessageRepositoryBase<InboxMessage, CoreBankDbContext>, IInboxMessageRepository
{
    public InboxMessageRepository(CoreBankDbContext dbContext, TimeProvider timeProvider, BusinessMetrics businessMetrics)
        : base(dbContext, timeProvider, businessMetrics)
    {
    }

    protected override DbSet<InboxMessage> InboxMessages => DbContext.InboxMessages;

    protected override BusinessMetrics.StoreName StoreName => BusinessMetrics.StoreName.CoreBankInbox;

    public Task<InboxMessage?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        InboxMessages.FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey, cancellationToken);

    public override Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken = default) =>
        base.ExecuteInTransactionAsync(operation, cancellationToken);
}
