namespace CoreBankDemo.Messaging;

/// <summary>
/// Narrow persistence port <see cref="InboxProcessorBase{TMessage}"/> depends
/// on (story 2.5; AD-2/AD-9) — exactly the operations the kernel's
/// poll/lock/dispatch loop needs, never the concrete
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/>-backed repository, so
/// the processor is unit-testable via Moq without any database. Mirrors
/// <see cref="IOutboxMessageStore{TMessage}"/> exactly (story 2.4's direct
/// pattern source). Implemented by
/// <see cref="InboxMessageRepositoryBase{TMessage,TDbContext}"/>, which
/// already provides every one of these members (inherited from
/// <see cref="MessageRepositoryBase{TMessage,TDbContext}"/>).
/// </summary>
public interface IInboxMessageStore<TMessage>
    where TMessage : class, IInboxMessage
{
    /// <summary>
    /// Claims up to <paramref name="batchSize"/> claimable rows in
    /// <paramref name="partitionId"/>, highest priority first and oldest first
    /// within a priority, atomically transitioning them to <c>Processing</c>. See
    /// <see cref="MessageRepositoryBase{TMessage,TDbContext}.ClaimBatchForPartitionAsync"/>.
    /// </summary>
    Task<IReadOnlyList<TMessage>> ClaimBatchForPartitionAsync(
        int partitionId, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handler-success transition: <c>Status = Completed</c>,
    /// <c>ProcessedAt</c> stamped from the store's <see cref="TimeProvider"/>.
    /// See <see cref="MessageRepositoryBase{TMessage,TDbContext}.MarkAsCompletedAsync"/>.
    /// </summary>
    Task<MessageTransitionOutcome> MarkAsCompletedAsync(
        TMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handler-failure retry/poison transition: retries below
    /// <see cref="MessageConstants.Defaults.MaxRetryCount"/>, terminal
    /// <see cref="MessageConstants.Status.Failed"/> at the limit. See
    /// <see cref="MessageRepositoryBase{TMessage,TDbContext}.MarkAsFailedWithRetryAsync"/>.
    /// </summary>
    Task<MessageTransitionOutcome> MarkAsFailedWithRetryAsync(
        TMessage message, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims exactly the row identified by <paramref name="id"/>, if it is
    /// currently <c>Pending</c> — mirrors
    /// <see cref="IOutboxMessageStore{TMessage}.TryClaimByIdAsync"/> exactly
    /// (spec: add-instant-payment-rail, review loop 1): an inline-execution
    /// entry point's claim, reusing the same optimistic-concurrency
    /// transition <see cref="ClaimBatchForPartitionAsync"/> uses (<c>Status</c>
    /// as a concurrency token), so an inline claim and a concurrent
    /// background batch claim can never both win the same row. See
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
    /// Claims the identified row only when it is first in its partition's
    /// dispatch order — <see cref="IMessage.Priority"/> descending, then
    /// arrival — so inline execution never overtakes earlier work of its own
    /// priority, while a higher-priority row may overtake queued
    /// lower-priority rows. See
    /// <see cref="MessageRepositoryBase{TMessage,TDbContext}.TryClaimByIdIfOldestAsync"/>.
    /// </summary>
    Task<TMessage?> TryClaimByIdIfOldestAsync(
        Guid id,
        int partitionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the row's <em>current</em> <c>Status</c> straight from the store.
    /// The inline paths use it between bounded claim attempts to tell "not
    /// first in dispatch order yet" (keep waiting) apart from "another
    /// claimant now owns the row" (stop waiting: it is being delivered
    /// elsewhere). Deliberately a projection, never an entity: the caller's
    /// scoped <c>DbContext</c> is the one that inserted the row moments ago,
    /// and an entity query would hand back that tracked instance -- still
    /// <c>Pending</c> whatever the database says -- so the wait would never
    /// end.
    /// </summary>
    Task<string?> GetStatusAsync(Guid id, CancellationToken cancellationToken = default);
}
