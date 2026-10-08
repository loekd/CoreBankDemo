using System.Diagnostics;
using System.Text.Json;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Accounts;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;

namespace CoreBankDemo.PaymentsAPI.Handlers;

/// <summary>
/// The inbox handler for CoreBank's stored <c>transaction-events</c> rows
/// (story 5.6; spec: payments-account-projection): dispatches by the frozen
/// wire <see cref="Constants"/> value (never a CLR type name), deserializes
/// the matching shared CloudEvent record from <see cref="InboxMessage.Payload"/>,
/// and enriches <see cref="Activity.Current"/> -- the consumer span
/// <see cref="InboxProcessorBase{TMessage}"/> already restored from the
/// message's persisted <c>TraceParent</c>/<c>TraceState</c> -- with the
/// approved per-event tags before emitting the approved structured log.
/// It owns business state -- the local account projection (settled balance
/// and reservation release) and the payment row's cached committed outcome
/// -- and its own completion: all of it, plus
/// <see cref="IInboxMessageRepository.MarkAsCompletedAsync"/> on this row,
/// commits in one database transaction (the transactional inbox). It still
/// never writes the outbox row's transport <c>Status</c>, never calls an
/// external service and never creates a second <see cref="ActivitySource"/>.
/// A malformed payload (invalid JSON or a JSON <c>null</c>), a stored event
/// type outside the four shared constants, or any failing write throws and
/// rolls the whole transaction back, so the kernel
/// (<see cref="InboxProcessorBase{TMessage}"/>) records the normal retry
/// transition -- retried without limit, never poisoned (ADR-023), so such a
/// row blocks its partition until it is fixed.
/// </summary>
internal sealed class TransactionEventHandler(
    ILogger<TransactionEventHandler> logger,
    IOutboxRepository outboxRepository,
    IInboxMessageRepository inboxRepository,
    IAccountProjectionStore accounts)
    : IInboxMessageHandler<InboxMessage>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    public async Task HandleAsync(InboxMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["IdempotencyKey"] = message.IdempotencyKey,
            ["PartitionId"] = message.PartitionId,
            ["EventType"] = message.EventType
        });

        // Transactional inbox (spec: payments-account-projection): the event's
        // effect on the account projection, the cached outcome on the payment
        // row and this inbox row's completion commit together or not at all.
        // The kernel's MarkAsCompletedAsync afterwards finds the row terminal
        // and does nothing -- the same arrangement as CoreBank's
        // TransactionExecutionHandler (story 4.6). Any exception rolls all of
        // it back and the kernel records a retry (ADR-023).
        await inboxRepository.ExecuteInTransactionAsync(async () =>
        {
            switch (message.EventType)
            {
                case Constants.TransactionCompleted:
                    await RecordCommittedOutcomeAsync(HandleTransactionCompleted(message), cancellationToken).ConfigureAwait(false);
                    break;
                case Constants.TransactionFailed:
                    await RecordCommittedOutcomeAsync(HandleTransactionFailed(message), cancellationToken).ConfigureAwait(false);
                    await ReleaseReservationAsync(message.TransactionId, cancellationToken).ConfigureAwait(false);
                    break;
                case Constants.BalanceUpdated:
                    await ApplyBalanceUpdatedAsync(HandleBalanceUpdated(message), cancellationToken).ConfigureAwait(false);
                    break;
                case Constants.TransactionCancelled:
                    // spec: instant-rail-cancelled-event -- a cancellation CoreBank
                    // committed is the residual 202's committed outcome. The
                    // repository refuses to overwrite a terminal cached payload, so
                    // a row the rail already marked Cancelled is a no-op, and a
                    // cached Cancelled is never overwritten by a later
                    // Completed/Failed either.
                    await RecordCommittedOutcomeAsync(HandleTransactionCancelled(message), cancellationToken).ConfigureAwait(false);
                    await ReleaseReservationAsync(message.TransactionId, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    // Never acknowledge a stored type this handler doesn't
                    // recognize (edge-case matrix) -- Story 5.5 only ever stores
                    // one of the four shared constants above, so reaching here
                    // means either the shared constants changed underneath this
                    // handler or the row was corrupted; either way this is a
                    // handler defect the kernel must retry (without limit, never
                    // poisoned: it blocks its partition until fixed -- ADR-023),
                    // never a silently accepted no-op.
                    throw new InvalidOperationException(
                        $"Unsupported stored transaction-events type '{message.EventType}' for inbox message {message.Id}.");
            }

            await inboxRepository.MarkAsCompletedAsync(message, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The debtor's settlement: CoreBank's reported balance already includes
    /// this debit, so the reservation for it is released in the same step. A
    /// creditor's event, or one for a transaction PaymentsAPI never accepted,
    /// only records the balance.
    /// </summary>
    private async Task ApplyBalanceUpdatedAsync(BalanceUpdatedEvent payload, CancellationToken cancellationToken)
    {
        await accounts.SettleAsync(payload.AccountNumber, payload.NewBalance, payload.Currency, cancellationToken).ConfigureAwait(false);
        var released = 0m;
        var payment = await outboxRepository.FindByIdempotencyKeyAsync(payload.TransactionId, cancellationToken).ConfigureAwait(false);
        if (payment is not null && payment.FromAccount == payload.AccountNumber)
        {
            released = await ReleaseAsync(payment, cancellationToken).ConfigureAwait(false);
        }

        var activity = Activity.Current;
        activity?.SetTag("account.settled_balance", payload.NewBalance);
        activity?.SetTag("account.released", released);
    }

    private async Task ReleaseReservationAsync(string transactionId, CancellationToken cancellationToken)
    {
        // PaymentsAPI stores TransactionId = IdempotencyKey (ADR-027), so the
        // indexed dedupe key finds the payment.
        var payment = await outboxRepository.FindByIdempotencyKeyAsync(transactionId, cancellationToken).ConfigureAwait(false);
        if (payment is not null)
        {
            await ReleaseAsync(payment, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<decimal> ReleaseAsync(OutboxMessage payment, CancellationToken cancellationToken)
    {
        var shortfall = await accounts.ReleaseAsync(payment.FromAccount, payment.Amount, cancellationToken).ConfigureAwait(false);
        if (shortfall > 0m)
        {
            // The projection is younger than the payment (recreated database):
            // nothing to block the partition over, but say so.
            logger.LogWarning(
                "Reservation for payment {TransactionId} on account {FromAccount} was short by {Shortfall}; clamped at zero",
                payment.TransactionId,
                payment.FromAccount,
                shortfall);
        }

        logger.LogInformation(
            "Released {Amount} reserved for payment {TransactionId} on account {FromAccount}",
            payment.Amount - shortfall,
            payment.TransactionId,
            payment.FromAccount);
        return payment.Amount - shortfall;
    }

    /// <summary>
    /// The instant rail's inline attempt regularly ends with CoreBankAPI
    /// answering <c>202 Pending</c> -- it accepted the command for its own
    /// background execution instead of running it inline -- and that
    /// non-committed answer is what gets cached on the payment row. CoreBank
    /// settles the transaction moments later and says so through exactly
    /// this event, so this is where the payment learns its real outcome;
    /// without it every duplicate replay answered <c>Pending</c> forever for
    /// a payment that had long since completed.
    /// </summary>
    private async Task RecordCommittedOutcomeAsync(
        (string TransactionId, string Status, DateTimeOffset ProcessedAt) outcome,
        CancellationToken cancellationToken)
    {
        var recorded = await outboxRepository
            .RecordCommittedOutcomeAsync(outcome.TransactionId, outcome.Status, outcome.ProcessedAt, cancellationToken)
            .ConfigureAwait(false);
        if (recorded)
        {
            logger.LogInformation(
                "Recorded committed outcome {Status} for payment {TransactionId} from its transaction event",
                outcome.Status,
                outcome.TransactionId);
        }
    }

    private (string TransactionId, string Status, DateTimeOffset ProcessedAt) HandleTransactionCompleted(InboxMessage message)
    {
        var payload = Deserialize<TransactionCompletedEvent>(message);

        var activity = Activity.Current;
        activity?.SetTag("transaction.id", payload.TransactionId);
        activity?.SetTag("event.type", message.EventType);
        activity?.SetTag("transaction.status", payload.Status);

        logger.LogInformation(
            "Transaction {TransactionId} completed with status {Status} for event {EventType}",
            payload.TransactionId,
            payload.Status,
            message.EventType);
        return (payload.TransactionId, payload.Status, payload.ProcessedAt);
    }

    private (string TransactionId, string Status, DateTimeOffset ProcessedAt) HandleTransactionFailed(InboxMessage message)
    {
        var payload = Deserialize<TransactionFailedEvent>(message);

        var activity = Activity.Current;
        activity?.SetTag("transaction.id", payload.TransactionId);
        activity?.SetTag("event.type", message.EventType);
        activity?.SetTag("transaction.status", payload.Status);
        // A null ErrorReason is valid; represent it explicitly so the expected
        // tag remains queryable rather than being removed by Activity.SetTag.
        activity?.SetTag("transaction.error_reason", payload.ErrorReason ?? string.Empty);
        // This event is the only place PaymentsAPI learns why CoreBank
        // rejected the payment, so it carries the traces dashboard's failed
        // payment tags for a rejection (FailedPaymentTags); the transaction.id
        // tag above already names the payment.
        activity?.SetTag(FailedPaymentTags.Outcome, FailedPaymentTags.Rejected);
        activity?.SetTag(FailedPaymentTags.FailureReason, payload.ErrorReason ?? string.Empty);

        logger.LogWarning(
            "Transaction {TransactionId} failed with status {Status}: {ErrorReason} for event {EventType}",
            payload.TransactionId,
            payload.Status,
            payload.ErrorReason,
            message.EventType);
        return (payload.TransactionId, payload.Status, payload.ProcessedAt);
    }

    private (string TransactionId, string Status, DateTimeOffset ProcessedAt) HandleTransactionCancelled(InboxMessage message)
    {
        var payload = Deserialize<TransactionCancelledEvent>(message);
        if (payload.Status != MessageConstants.Status.Cancelled)
        {
            // Only the wire word Cancelled may become a cached committed
            // outcome: anything else on a transaction.cancelled event is a
            // producer defect the kernel must retry (without limit, never
            // poisoned -- ADR-023), never a status this handler forwards into
            // the payment row (same philosophy as the unsupported-type arm).
            throw new InvalidOperationException(
                $"transaction.cancelled event for transaction '{payload.TransactionId}' (inbox message {message.Id}) carries status '{payload.Status}' instead of '{MessageConstants.Status.Cancelled}'.");
        }

        var activity = Activity.Current;
        activity?.SetTag("transaction.id", payload.TransactionId);
        activity?.SetTag("event.type", message.EventType);
        activity?.SetTag("transaction.status", payload.Status);
        // A null Reason is valid; represent it explicitly so the tag remains
        // queryable rather than being removed by Activity.SetTag.
        activity?.SetTag("transaction.cancel_reason", payload.Reason ?? string.Empty);

        logger.LogInformation(
            "Transaction {TransactionId} was cancelled by CoreBank with status {Status}: {Reason} for event {EventType}",
            payload.TransactionId,
            payload.Status,
            payload.Reason,
            message.EventType);
        return (payload.TransactionId, payload.Status, payload.ProcessedAt);
    }

    private BalanceUpdatedEvent HandleBalanceUpdated(InboxMessage message)
    {
        var payload = Deserialize<BalanceUpdatedEvent>(message);

        var activity = Activity.Current;
        activity?.SetTag("transaction.id", payload.TransactionId);
        activity?.SetTag("event.type", message.EventType);
        activity?.SetTag("account.number", payload.AccountNumber);
        activity?.SetTag("account.delta", payload.Delta);
        activity?.SetTag("account.new_balance", payload.NewBalance);
        activity?.SetTag("account.currency", payload.Currency);

        logger.LogInformation(
            "Account {AccountNumber} balance updated by {Delta} to {NewBalance} {Currency} for transaction {TransactionId} from event {EventType}",
            payload.AccountNumber,
            payload.Delta,
            payload.NewBalance,
            payload.Currency,
            payload.TransactionId,
            message.EventType);
        return payload;
    }

    /// <summary>
    /// Strict deserialization: <see cref="JsonSerializer.Deserialize{TValue}(string,JsonSerializerOptions?)"/>
    /// already throws <see cref="JsonException"/> for invalid JSON, and a
    /// JSON <c>null</c> literal deserializes successfully to a
    /// <see langword="null"/> record reference -- both must throw here
    /// (edge-case matrix's malformed-payload row) rather than let a null
    /// payload reach a handler method and NRE.
    /// </summary>
    private static TEvent Deserialize<TEvent>(InboxMessage message)
    {
        var payload = JsonSerializer.Deserialize<TEvent>(message.Payload, SerializerOptions);
        if (payload is null)
        {
            throw new InvalidOperationException(
                $"Malformed payload for transaction-events type '{message.EventType}' (inbox message {message.Id}): payload deserialized to null.");
        }

        return payload;
    }
}
