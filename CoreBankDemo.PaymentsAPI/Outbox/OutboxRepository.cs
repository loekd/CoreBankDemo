using System.Text.Json;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Accounts;
using CoreBankDemo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.PaymentsAPI.Outbox;

/// <summary>Outcome of <see cref="IOutboxRepository.AcceptAsync"/>.</summary>
internal enum PaymentAcceptance
{
    /// <summary>Row inserted and the debtor's reservation raised, in one commit.</summary>
    Stored,
    /// <summary>The idempotency key already exists; nothing written. The balance was never consulted.</summary>
    Duplicate,
    /// <summary>The debtor's known available balance is short; nothing written, key not consumed.</summary>
    InsufficientFunds
}

internal interface IOutboxRepository
{
    Task<bool> StoreIfNewAsync(OutboxMessage message, CancellationToken cancellationToken);

    Task<OutboxMessage?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records the business outcome CoreBankAPI committed for
    /// <paramref name="transactionId"/>, as learned from its
    /// <c>transaction.completed</c>/<c>transaction.failed</c>/
    /// <c>transaction.cancelled</c> event, on the payment's
    /// <see cref="OutboxMessage.ResponsePayload"/> -- the same serialized
    /// <see cref="TransactionSubmission"/> shape the delivery path persists,
    /// so a later duplicate replay resolves it identically. Only ever
    /// upgrades a missing or non-terminal cached outcome; a payload that
    /// already carries <c>Completed</c>/<c>Failed</c>/<c>Cancelled</c> is
    /// left untouched (spec: instant-rail-cancelled-event keeps a cached
    /// cancellation immutable in both directions).
    /// Never touches <see cref="OutboxMessage.Status"/> (AD-11: transport
    /// state only).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the row was updated; <see langword="false"/>
    /// when no row exists for the id or its cached outcome was already terminal.
    /// </returns>
    Task<bool> RecordCommittedOutcomeAsync(
        string transactionId,
        string status,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// The transactional-outbox accept (spec: payments-account-projection,
    /// ADR-028): inserts <paramref name="message"/> and raises the debtor's
    /// <see cref="Accounts.ProjectedAccount.Reserved"/> in one database
    /// transaction. The insert runs first, so a duplicate key is detected by
    /// the unique index before any balance is read (AD-4), and a refusal rolls
    /// the insert back so no row and no reservation remain.
    /// </summary>
    Task<PaymentAcceptance> AcceptAsync(OutboxMessage message, CancellationToken cancellationToken);
}

internal sealed class OutboxRepository(PaymentsDbContext dbContext, TimeProvider timeProvider, BusinessMetrics businessMetrics)
    : OutboxMessageRepositoryBase<OutboxMessage, PaymentsDbContext>(dbContext, timeProvider, businessMetrics), IOutboxRepository
{
    protected override DbSet<OutboxMessage> OutboxMessages => DbContext.OutboxMessages;

    protected override BusinessMetrics.StoreName StoreName => BusinessMetrics.StoreName.PaymentsOutbox;

    public Task<OutboxMessage?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        OutboxMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(message => message.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<bool> RecordCommittedOutcomeAsync(
        string transactionId,
        string status,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken)
    {
        var message = await OutboxMessages
            .SingleOrDefaultAsync(row => row.TransactionId == transactionId, cancellationToken)
            .ConfigureAwait(false);
        if (message is null || HasCommittedOutcome(message.ResponsePayload))
        {
            return false;
        }

        // Same serializer defaults as HttpForwardOutboxDeliveryStrategy, so
        // PaymentsController.ResolveDeliveredResponse reads both alike.
        message.ResponsePayload = JsonSerializer.Serialize(
            new TransactionSubmission(transactionId, status, processedAt));

        // A DbUpdateConcurrencyException (Status is the row's concurrency
        // token, and the outbox processor may be transitioning it right now)
        // deliberately propagates: the inbox kernel then retries the event on
        // its next tick, which is exactly the right outcome for a lost race.
        await DbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Cancelled is terminal too (spec: instant-rail-timeout-cancel): a cached
    // cancellation is never overwritten by a later event. A corrupt payload is
    // never a committed outcome; it is overwritten.
    private static bool HasCommittedOutcome(string? responsePayload) =>
        CachedTransactionOutcome.TryRead(responsePayload) is { } cached
        && CachedTransactionOutcome.IsCommitted(cached.Status);

    public async Task<PaymentAcceptance> AcceptAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            await ExecuteInTransactionAsync(async () =>
            {
                // 1. Insert first (AD-4). A unique violation aborts the
                //    PostgreSQL transaction, so the only way out is a rollback.
                OutboxMessages.Add(message);
                try
                {
                    await DbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException ex) when (UniqueViolation.IsUniqueViolation(ex))
                {
                    throw new AcceptanceRollback(PaymentAcceptance.Duplicate);
                }

                // 2. Lock the debtor's projection row (created if unseen).
                var now = timeProvider.GetUtcNow().UtcDateTime;
                var account = await ProjectedAccountRows
                    .LockAsync(DbContext, message.FromAccount, now, cancellationToken).ConfigureAwait(false);

                // 3. Refuse only what the projection knows is short.
                if (account.SettledBalance is { } settled && settled - account.Reserved < message.Amount)
                {
                    throw new AcceptanceRollback(PaymentAcceptance.InsufficientFunds);
                }

                // 4. Reserve and commit with the row.
                account.Reserved += message.Amount;
                account.UpdatedAt = now;
                await DbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceRollback rollback)
        {
            DbContext.ChangeTracker.Clear();
            if (rollback.Outcome == PaymentAcceptance.Duplicate)
            {
                // Only a dedupe hit is a store "duplicate"; a refusal is
                // counted by the payment-intake metric, not by the store.
                businessMetrics.RecordStoreOperation(
                    StoreName, BusinessMetrics.StoreKind.Outbox, BusinessMetrics.StoreOperationOutcome.Duplicate);
            }

            return rollback.Outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DbContext.ChangeTracker.Clear();
            throw;
        }
        catch
        {
            DbContext.ChangeTracker.Clear();
            businessMetrics.RecordStoreOperation(
                StoreName, BusinessMetrics.StoreKind.Outbox, BusinessMetrics.StoreOperationOutcome.Failed);
            throw;
        }

        businessMetrics.RecordStoreOperation(
            StoreName, BusinessMetrics.StoreKind.Outbox, BusinessMetrics.StoreOperationOutcome.Added);
        return PaymentAcceptance.Stored;
    }

    /// <summary>
    /// Carries a deliberate rollback out of <see cref="ExecuteInTransactionAsync"/>;
    /// never escapes <see cref="AcceptAsync"/>. Not transient, so the Npgsql
    /// execution strategy never retries it.
    /// </summary>
    private sealed class AcceptanceRollback(PaymentAcceptance outcome) : Exception
    {
        public PaymentAcceptance Outcome { get; } = outcome;
    }
}
