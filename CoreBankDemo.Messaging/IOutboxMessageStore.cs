namespace CoreBankDemo.Messaging;

/// <summary>
/// Narrow persistence port <see cref="OutboxProcessorBase{TMessage}"/> depends
/// on (story 2.4; AD-2/AD-9) — exactly the operations the kernel's
/// poll/lock/dispatch loop needs, never the concrete
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/>-backed repository, so
/// the processor is unit-testable via Moq without any database. Implemented by
/// <see cref="OutboxMessageRepositoryBase{TMessage,TDbContext}"/>, which
/// already provides every one of these members (inherited from
/// <see cref="MessageRepositoryBase{TMessage,TDbContext}"/>).
/// </summary>
public interface IOutboxMessageStore<TMessage>
    where TMessage : class, IOutboxMessage
{
    /// <summary>
    /// Claims up to <paramref name="batchSize"/> claimable rows in
    /// <paramref name="partitionId"/>, oldest first, atomically transitioning
    /// them to <c>Processing</c>. See
    /// <see cref="MessageRepositoryBase{TMessage,TDbContext}.ClaimBatchForPartitionAsync"/>.
    /// </summary>
    Task<IReadOnlyList<TMessage>> ClaimBatchForPartitionAsync(
        int partitionId, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transport-success transition: <c>Status = Completed</c>,
    /// <c>ProcessedAt</c> stamped from the store's <see cref="TimeProvider"/>.
    /// See <see cref="MessageRepositoryBase{TMessage,TDbContext}.MarkAsCompletedAsync"/>.
    /// </summary>
    Task<MessageTransitionOutcome> MarkAsCompletedAsync(
        TMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transport-failure retry/poison transition: retries below
    /// <see cref="MessageConstants.Defaults.MaxRetryCount"/>, terminal
    /// <see cref="MessageConstants.Status.Failed"/> at the limit. See
    /// <see cref="MessageRepositoryBase{TMessage,TDbContext}.MarkAsFailedWithRetryAsync"/>.
    /// </summary>
    Task<MessageTransitionOutcome> MarkAsFailedWithRetryAsync(
        TMessage message, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims exactly the row identified by <paramref name="id"/>, if it is
    /// currently <c>Pending</c> — the instant-payment-rail inline delivery
    /// path's claim (spec: add-instant-payment-rail): reuses the same
    /// optimistic-concurrency transition <see cref="ClaimBatchForPartitionAsync"/>
    /// uses (<c>Status</c> as a concurrency token), so an inline claim and a
    /// concurrent background batch claim can never both win the same row —
    /// exactly-once stays a property of the claim itself, never the
    /// receiver's dedupe. See
    /// <see cref="MessageRepositoryBase{TMessage,TDbContext}.TryClaimByIdAsync"/>.
    /// </summary>
    /// <returns>
    /// The claimed, now-<c>Processing</c> row on success; <see langword="null"/>
    /// when no such row exists, it is not currently <c>Pending</c> (already
    /// claimed by a concurrent caller, or already terminal), or a concurrent
    /// caller won the claim race for it.
    /// </returns>
    Task<TMessage?> TryClaimByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the identified row only when it is the oldest currently
    /// claimable row in its partition. Used by the instant inline path while
    /// holding the same distributed partition lock as the background
    /// processor, so inline settlement cannot overtake earlier durable work.
    /// </summary>
    Task<TMessage?> TryClaimByIdIfOldestAsync(
        Guid id,
        int partitionId,
        CancellationToken cancellationToken = default);
}
