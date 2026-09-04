using System.Diagnostics;
using System.Text.Json;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;

namespace CoreBankDemo.PaymentsAPI.Handlers;

/// <summary>
/// Story 5.6's observational-only inbox handler: dispatches Story 5.5's
/// stored <c>transaction-events</c> rows by their frozen wire
/// <see cref="Constants"/> value (never a CLR type name -- design notes),
/// deserializes the matching shared CloudEvent record from
/// <see cref="InboxMessage.Payload"/>, and enriches
/// <see cref="Activity.Current"/> -- the consumer span
/// <see cref="InboxProcessorBase{TMessage}"/> already restored from the
/// message's persisted <c>TraceParent</c>/<c>TraceState"/> -- with the
/// approved per-event tags before emitting the approved structured log.
/// Never mutates payment or account state, never calls an external service,
/// and never creates a second <see cref="ActivitySource"/>: this handler
/// only observes. A malformed payload (invalid JSON or a JSON <c>null</c>)
/// or a stored event type outside the three shared constants throws, so the
/// kernel (<see cref="InboxProcessorBase{TMessage}"/>) records the normal
/// retry/poison transition -- this handler itself decides nothing about
/// <see cref="InboxMessage.Status"/>.
/// </summary>
internal sealed class TransactionEventHandler(
    ILogger<TransactionEventHandler> logger,
    IOutboxRepository outboxRepository)
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

        switch (message.EventType)
        {
            case Constants.TransactionCompleted:
                await RecordCommittedOutcomeAsync(HandleTransactionCompleted(message), cancellationToken).ConfigureAwait(false);
                break;
            case Constants.TransactionFailed:
                await RecordCommittedOutcomeAsync(HandleTransactionFailed(message), cancellationToken).ConfigureAwait(false);
                break;
            case Constants.BalanceUpdated:
                HandleBalanceUpdated(message);
                break;
            default:
                // Never acknowledge a stored type this handler doesn't
                // recognize (edge-case matrix) -- Story 5.5 only ever stores
                // one of the three shared constants above, so reaching here
                // means either the shared constants changed underneath this
                // handler or the row was corrupted; either way this is a
                // handler defect the kernel must retry/poison, never a
                // silently accepted no-op.
                throw new InvalidOperationException(
                    $"Unsupported stored transaction-events type '{message.EventType}' for inbox message {message.Id}.");
        }
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

        logger.LogWarning(
            "Transaction {TransactionId} failed with status {Status}: {ErrorReason} for event {EventType}",
            payload.TransactionId,
            payload.Status,
            payload.ErrorReason,
            message.EventType);
        return (payload.TransactionId, payload.Status, payload.ProcessedAt);
    }

    private void HandleBalanceUpdated(InboxMessage message)
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
