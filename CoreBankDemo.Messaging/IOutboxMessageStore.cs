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
}
