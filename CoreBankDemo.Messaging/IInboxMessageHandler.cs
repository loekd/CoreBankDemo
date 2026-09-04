namespace CoreBankDemo.Messaging;

/// <summary>
/// Handler port <see cref="InboxProcessorBase{TMessage}"/> depends on (story
/// 2.5; AD-3). Mirrors <see cref="IOutboxDeliveryStrategy{TMessage}"/>'s
/// success/failure contract exactly (AD-11 applied symmetrically): a handler
/// simply attempts to process <paramref name="message"/>, returns normally on
/// success, and throws any exception on failure. Success/failure
/// <em>classification</em> (retry vs. terminal Failed) belongs to the kernel
/// alone, never the handler — <see cref="InboxProcessorBase{TMessage}"/> is
/// the only place that decides what a thrown exception means for the
/// message's <c>Status</c>.
///
/// <para>
/// Unlike <see cref="IOutboxDeliveryStrategy{TMessage}"/>, which is
/// ctor-injected once as a singleton, an <see cref="IInboxMessageHandler{TMessage}"/>
/// is resolved fresh per message from a per-message DI scope (see
/// <see cref="InboxProcessorBase{TMessage}"/>'s remarks) — the legacy inbox
/// never scoped a handler per message (FR-19; AD-3), so a handler may safely
/// depend on scoped services (e.g. a fresh <see cref="Microsoft.EntityFrameworkCore.DbContext"/>)
/// without those dependencies leaking or being shared across messages.
/// </para>
/// </summary>
public interface IInboxMessageHandler<TMessage>
    where TMessage : class, IInboxMessage
{
    /// <summary>
    /// Attempts to handle <paramref name="message"/>. Returns normally on
    /// success. Throws any exception to signal failure — the exception's
    /// message becomes the row's <c>LastError</c>
    /// (<see cref="MessageRepositoryBase{TMessage,TDbContext}.MarkAsFailedWithRetryAsync"/>);
    /// the original exception itself is never rethrown out of the processor's
    /// tick.
    /// </summary>
    Task HandleAsync(TMessage message, CancellationToken cancellationToken = default);
}
