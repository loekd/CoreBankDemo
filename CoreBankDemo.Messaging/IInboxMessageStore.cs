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
    /// <paramref name="partitionId"/>, oldest first, atomically transitioning
    /// them to <c>Processing</c>. See
    /// <see cref="MessageRepositoryBase{TMessage,TDbContext}.ClaimBatchForPartitionAsync"/>.
    /// </summary>
    Task<IReadOnlyList<TMessage>> ClaimBatchForPartitionAsync(
        int partitionId, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handler-success transition: <c>Status = Completed</c>,
    /// <c>ProcessedAt</c> stamped from the store's <see cref="TimeProvider"/>.
    /// See <see cref="MessageRepositoryBase{TMessage,TDbContext}.MarkAsCompletedAsync"/>.
    /// </summary>
    Task MarkAsCompletedAsync(TMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handler-failure retry/poison transition: retries below
    /// <see cref="MessageConstants.Defaults.MaxRetryCount"/>, terminal
    /// <see cref="MessageConstants.Status.Failed"/> at the limit. See
    /// <see cref="MessageRepositoryBase{TMessage,TDbContext}.MarkAsFailedWithRetryAsync"/>.
    /// </summary>
    Task MarkAsFailedWithRetryAsync(TMessage message, string errorMessage, CancellationToken cancellationToken = default);
}
