using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.PaymentsAPI.Inbox;

/// <summary>
/// Narrow port <see cref="Handlers.TransactionEventIntakeHandler"/> depends on
/// (spec-5-5). <see cref="StoreIfNewAsync"/> is satisfied automatically by
/// <see cref="InboxMessageRepositoryBase{TMessage,TDbContext}"/>'s inherited,
/// already race-safe implementation (AD-4) -- this narrow port exists so the
/// handler is mockable without a database, mirroring
/// <see cref="Outbox.IOutboxRepository"/> and CoreBankAPI's
/// <c>IInboxMessageRepository</c> (story 4.4) exactly.
/// </summary>
internal interface IInboxMessageRepository
{
    Task<bool> StoreIfNewAsync(InboxMessage message, CancellationToken cancellationToken);

    /// <summary>Kernel transaction helper, exposed so <c>TransactionEventHandler</c> can make its projection writes and the row's completion one commit (spec: payments-account-projection).</summary>
    Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken);

    /// <summary>The kernel's own completion transition; called by the handler inside its transaction so the processor's later call finds the row <see cref="MessageTransitionOutcome.AlreadyTerminal"/>.</summary>
    Task<MessageTransitionOutcome> MarkAsCompletedAsync(InboxMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Dual-role store (spec-5-5, mirroring spec-4-4): the same class is both
/// this story's intake repository (<see cref="IInboxMessageRepository"/>)
/// and, unmodified, the future kernel inbox processor's store (story 5.6, via
/// the inherited <see cref="InboxMessageRepositoryBase{TMessage,TDbContext}"/>
/// members exposed through <see cref="IInboxMessageStore{TMessage}"/>).
/// </summary>
internal sealed class InboxMessageRepository(PaymentsDbContext dbContext, TimeProvider timeProvider, BusinessMetrics businessMetrics)
    : InboxMessageRepositoryBase<InboxMessage, PaymentsDbContext>(dbContext, timeProvider, businessMetrics), IInboxMessageRepository
{
    protected override DbSet<InboxMessage> InboxMessages => DbContext.InboxMessages;

    protected override BusinessMetrics.StoreName StoreName => BusinessMetrics.StoreName.PaymentsInbox;
}
