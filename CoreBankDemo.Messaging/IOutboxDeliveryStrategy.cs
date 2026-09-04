namespace CoreBankDemo.Messaging;

/// <summary>
/// Delivery port <see cref="OutboxProcessorBase{TMessage}"/> depends on (story
/// 2.4; AD-3, AD-11): concrete transports (HTTP-forward, Dapr-publish — epics
/// 4/5) plug in here as strategies instead of each reimplementing the
/// poll/lock/dispatch loop — the AD-2 defect this story exists to fix.
/// Success/failure <em>classification</em> (retry vs. terminal Failed) belongs
/// to the kernel alone, never the strategy: a strategy simply attempts
/// delivery, returns normally on success, and throws any exception on
/// failure. <see cref="OutboxProcessorBase{TMessage}"/> is the only place that
/// decides what a thrown exception means for the message's <c>Status</c>.
/// </summary>
public interface IOutboxDeliveryStrategy<TMessage>
    where TMessage : class, IOutboxMessage
{
    /// <summary>
    /// Attempts to deliver <paramref name="message"/>. Returns normally on
    /// success. Throws any exception to signal failure — the exception's
    /// message becomes the row's <c>LastError</c>
    /// (<see cref="MessageRepositoryBase{TMessage,TDbContext}.MarkAsFailedWithRetryAsync"/>);
    /// the original exception itself is never rethrown out of the processor's
    /// tick.
    /// </summary>
    Task DeliverAsync(TMessage message, CancellationToken cancellationToken = default);
}
