using System.Diagnostics.Metrics;

namespace CoreBankDemo.ServiceDefaults;

/// <summary>
/// Story 6.5's single shared business-metrics abstraction, backed by
/// <see cref="System.Diagnostics.Metrics"/>. Owns the <see cref="Meter"/> and
/// every instrument's name/unit/description exactly once, and exposes typed
/// recording methods only — callers never construct a
/// <c>KeyValuePair&lt;string, object?&gt;</c>/<c>TagList</c> themselves, so
/// the closed low-cardinality attribute vocabulary (this class's nested
/// outcome/store/transport enums) can never drift or grow an unbounded tag.
/// Registered as a DI singleton by <c>AddServiceDefaults</c> and subscribed
/// into the OpenTelemetry metrics pipeline via <see cref="MeterName"/>,
/// mirroring how the existing framework <c>Meter</c>s (ASP.NET Core, runtime,
/// HttpClient instrumentation) are already wired.
///
/// <para>
/// Never put transaction ids, idempotency keys, account numbers, trace/span
/// ids, exception messages/types, URLs, lock names, arbitrary currencies, or
/// any other user-controlled/unbounded value into a metric attribute — every
/// attribute value recorded here comes from one of this class's own closed
/// enums. A caller that needs to record an unsupported combination is a sign
/// the contract needs to change, not a reason to bypass this API.
/// </para>
/// </summary>
public sealed class BusinessMetrics : IDisposable
{
    /// <summary>The <see cref="Meter"/> name every composition root registers with <c>WithMetrics(m =&gt; m.AddMeter(...))</c>.</summary>
    public const string MeterName = "CoreBankDemo.Business";

    public const string PaymentIntakeInstrumentName = "corebankdemo.payment.intake";
    public const string TransactionIntakeInstrumentName = "corebankdemo.transaction.intake";
    public const string TransactionProcessedInstrumentName = "corebankdemo.transaction.processed";
    public const string MessagingStoreOperationsInstrumentName = "corebankdemo.messaging.store.operations";
    public const string MessagingItemsProcessedInstrumentName = "corebankdemo.messaging.items.processed";
    public const string MessagingQueueDurationInstrumentName = "corebankdemo.messaging.queue.duration";
    public const string MessagingDeliveriesInstrumentName = "corebankdemo.messaging.deliveries";
    public const string PaymentInstantDurationInstrumentName = "corebankdemo.payment.instant.duration";

    /// <summary>Outcome of payments intake (spec-6-5 metric contract).</summary>
    public enum PaymentOutcome
    {
        Stored,
        Duplicate,
        ValidationFailed
    }

    /// <summary>
    /// Payment rail a request declared (spec: add-instant-payment-rail's
    /// metric contract) -- closed two-value set, always derived from
    /// PaymentsAPI's own already-validated closed <c>Scheme</c> set, never
    /// copied from request data verbatim.
    /// </summary>
    public enum PaymentScheme
    {
        Standard,
        Instant
    }

    /// <summary>
    /// Authoritative outcome of one instant-rail request's inline attempt
    /// (spec: add-instant-payment-rail's metric contract):
    /// <see cref="Settled"/> is a committed business success,
    /// <see cref="Rejected"/> is a committed business rejection (still a
    /// successfully processed message per AD-11), and <see cref="Deferred"/>
    /// covers everything that falls back to the background rail (budget
    /// exhaustion, a transport failure, the row already being claimed, or the
    /// instant rail being disabled).
    /// </summary>
    public enum InstantPaymentOutcome
    {
        Settled,
        Rejected,
        Deferred
    }

    /// <summary>Outcome of CoreBank transaction intake (spec-6-5 metric contract).</summary>
    public enum TransactionIntakeOutcome
    {
        Accepted,
        Replayed,
        InFlight,
        TransportFailed
    }

    /// <summary>Outcome of a committed transaction-execution transaction (spec-6-5 metric contract).</summary>
    public enum TransactionProcessedOutcome
    {
        Completed,
        BusinessRejected
    }

    /// <summary>Durable message store kind — never inferred from a CLR type name (design notes).</summary>
    public enum StoreKind
    {
        Inbox,
        Outbox
    }

    /// <summary>
    /// Stable durable-store identity. Deliberately distinct from a store's
    /// distributed-lock name prefix (e.g. CoreBank's messaging outbox locks
    /// under <c>messaging-outbox</c> but reports its business store identity
    /// as <see cref="CoreBankOutbox"/>) — concrete repositories/processors
    /// supply this explicitly rather than it being derived from any other
    /// identifier.
    /// </summary>
    public enum StoreName
    {
        PaymentsOutbox,
        CoreBankInbox,
        CoreBankOutbox,
        PaymentsInbox
    }

    /// <summary>Outcome of a store-level insert/dedupe attempt (spec-6-5 metric contract).</summary>
    public enum StoreOperationOutcome
    {
        Added,
        Duplicate,
        Failed
    }

    /// <summary>Authoritative processing outcome for one claimed inbox/outbox item (spec-6-5 metric contract).</summary>
    public enum ItemOutcome
    {
        Completed,
        RetryScheduled,
        TerminalFailed,
        CompletionPersistenceFailed,
        RetryPersistenceFailed
    }

    /// <summary>Direction of a concrete HTTP/Dapr transport attempt.</summary>
    public enum DeliveryDirection
    {
        Sent,
        Received
    }

    /// <summary>Concrete transport used for a delivery attempt.</summary>
    public enum Transport
    {
        Http,
        Dapr
    }

    /// <summary>
    /// Closed set of message "types" a delivery can carry — the transaction
    /// command plus the existing CloudEvent type constants. Never populated
    /// from an incoming CloudEvent's own <c>type</c> string verbatim; an
    /// unrecognized incoming type is always reported as <see cref="Unknown"/>.
    /// </summary>
    public enum MessageType
    {
        TransactionCommand,
        TransactionCompleted,
        TransactionFailed,
        BalanceUpdated,
        Unknown
    }

    /// <summary>Outcome of a concrete HTTP/Dapr delivery attempt (spec-6-5 metric contract).</summary>
    public enum DeliveryOutcome
    {
        Succeeded,
        Failed,
        Duplicate,
        Unknown
    }

    private readonly Meter _meter;
    private readonly Counter<long> _paymentIntake;
    private readonly Counter<long> _transactionIntake;
    private readonly Counter<long> _transactionProcessed;
    private readonly Counter<long> _storeOperations;
    private readonly Counter<long> _itemsProcessed;
    private readonly Histogram<double> _queueDuration;
    private readonly Counter<long> _deliveries;
    private readonly Histogram<double> _instantPaymentDuration;

    /// <summary>
    /// Exposed only for <see cref="System.Diagnostics.Metrics.MeterListener"/>-based
    /// tests to scope observation to this specific instance's own <see cref="Meter"/>
    /// (by reference) rather than <see cref="MeterName"/> alone — multiple
    /// <see cref="BusinessMetrics"/> instances share the same meter name (each
    /// composition root and, under parallel test execution, each test's own
    /// instance), and <see cref="System.Diagnostics.Metrics.MeterListener"/>
    /// subscribes process-wide by name, so name-only filtering would leak
    /// measurements across concurrently-running tests.
    /// </summary>
    internal Meter Meter => _meter;

    public BusinessMetrics()
    {
        _meter = new Meter(MeterName);

        _paymentIntake = _meter.CreateCounter<long>(
            PaymentIntakeInstrumentName,
            unit: "{payment}",
            description: "Payments accepted at intake, rejected as duplicates, or rejected by validation.");

        _transactionIntake = _meter.CreateCounter<long>(
            TransactionIntakeInstrumentName,
            unit: "{transaction}",
            description: "CoreBank transaction commands accepted, replayed, in-flight, or transport-failed at intake.");

        _transactionProcessed = _meter.CreateCounter<long>(
            TransactionProcessedInstrumentName,
            unit: "{transaction}",
            description: "CoreBank transactions whose ledger/inbox/event transaction committed, by business outcome.");

        _storeOperations = _meter.CreateCounter<long>(
            MessagingStoreOperationsInstrumentName,
            unit: "{operation}",
            description: "Inbox/outbox store insert attempts, by dedupe/failure outcome.");

        _itemsProcessed = _meter.CreateCounter<long>(
            MessagingItemsProcessedInstrumentName,
            unit: "{item}",
            description: "Inbox/outbox items that reached an authoritative processing outcome.");

        _queueDuration = _meter.CreateHistogram<double>(
            MessagingQueueDurationInstrumentName,
            unit: "ms",
            description: "Time a claimed inbox/outbox item waited in its store before handling/delivery started.");

        _deliveries = _meter.CreateCounter<long>(
            MessagingDeliveriesInstrumentName,
            unit: "{delivery}",
            description: "Concrete HTTP/Dapr send or receive attempts, by transport outcome.");

        _instantPaymentDuration = _meter.CreateHistogram<double>(
            PaymentInstantDurationInstrumentName,
            unit: "ms",
            description: "Elapsed time of one instant-rail request's inline attempt, from claim to conclusion or budget expiry.");
    }

    /// <summary>Records one payments-intake outcome. Recorded after the handler outcome is known.</summary>
    public void RecordPaymentIntake(PaymentOutcome outcome, PaymentScheme scheme) =>
        _paymentIntake.Add(
            1,
            new KeyValuePair<string, object?>("outcome", ToTag(outcome)),
            new KeyValuePair<string, object?>("payment.scheme", ToTag(scheme)));

    /// <summary>Records one CoreBank transaction-intake outcome. Recorded after the handler outcome is known.</summary>
    public void RecordTransactionIntake(TransactionIntakeOutcome outcome) =>
        _transactionIntake.Add(1, new KeyValuePair<string, object?>("outcome", ToTag(outcome)));

    /// <summary>Records one committed transaction-execution outcome. Recorded only after the enclosing transaction commits.</summary>
    public void RecordTransactionProcessed(TransactionProcessedOutcome outcome) =>
        _transactionProcessed.Add(1, new KeyValuePair<string, object?>("outcome", ToTag(outcome)));

    /// <summary>Records one store-level insert/dedupe outcome. Recorded after the outcome is known, before any rethrow on failure.</summary>
    public void RecordStoreOperation(StoreName storeName, StoreKind storeKind, StoreOperationOutcome outcome) =>
        _storeOperations.Add(
            1,
            new KeyValuePair<string, object?>("messaging.store.name", ToTag(storeName)),
            new KeyValuePair<string, object?>("messaging.store.kind", ToTag(storeKind)),
            new KeyValuePair<string, object?>("outcome", ToTag(outcome)));

    /// <summary>Records one authoritative inbox/outbox item-processing outcome.</summary>
    public void RecordItemProcessed(StoreName storeName, StoreKind storeKind, ItemOutcome outcome) =>
        _itemsProcessed.Add(
            1,
            new KeyValuePair<string, object?>("messaging.store.name", ToTag(storeName)),
            new KeyValuePair<string, object?>("messaging.store.kind", ToTag(storeKind)),
            new KeyValuePair<string, object?>("outcome", ToTag(outcome)));

    /// <summary>
    /// Records how long a claimed item waited in its store before
    /// handling/delivery started. A negative duration (a durable timestamp
    /// later than the current <see cref="TimeProvider"/> reading) is clamped
    /// to <c>0</c> ms rather than recorded as a negative histogram value.
    /// </summary>
    public void RecordQueueDuration(StoreName storeName, StoreKind storeKind, TimeSpan queueDuration)
    {
        var milliseconds = Math.Max(0d, queueDuration.TotalMilliseconds);
        _queueDuration.Record(
            milliseconds,
            new KeyValuePair<string, object?>("messaging.store.name", ToTag(storeName)),
            new KeyValuePair<string, object?>("messaging.store.kind", ToTag(storeKind)));
    }

    /// <summary>
    /// Records one instant-rail request's inline-attempt duration, tagged by
    /// its authoritative outcome. Recorded exactly once per instant-rail
    /// request -- when the inline attempt concludes (settled/rejected) or the
    /// budget/attempts are exhausted (deferred) -- never for a request
    /// abandoned solely because the caller cancelled.
    /// </summary>
    public void RecordInstantPaymentDuration(InstantPaymentOutcome outcome, TimeSpan duration)
    {
        var milliseconds = Math.Max(0d, duration.TotalMilliseconds);
        _instantPaymentDuration.Record(
            milliseconds,
            new KeyValuePair<string, object?>("outcome", ToTag(outcome)));
    }

    /// <summary>Records one concrete HTTP/Dapr send or receive attempt outcome.</summary>
    public void RecordDelivery(DeliveryDirection direction, Transport transport, MessageType messageType, DeliveryOutcome outcome) =>
        _deliveries.Add(
            1,
            new KeyValuePair<string, object?>("messaging.direction", ToTag(direction)),
            new KeyValuePair<string, object?>("messaging.transport", ToTag(transport)),
            new KeyValuePair<string, object?>("messaging.message.type", ToTag(messageType)),
            new KeyValuePair<string, object?>("outcome", ToTag(outcome)));

    private static string ToTag(PaymentOutcome outcome) => outcome switch
    {
        PaymentOutcome.Stored => "stored",
        PaymentOutcome.Duplicate => "duplicate",
        PaymentOutcome.ValidationFailed => "validation_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    private static string ToTag(PaymentScheme scheme) => scheme switch
    {
        PaymentScheme.Standard => "standard",
        PaymentScheme.Instant => "instant",
        _ => throw new ArgumentOutOfRangeException(nameof(scheme), scheme, null)
    };

    private static string ToTag(InstantPaymentOutcome outcome) => outcome switch
    {
        InstantPaymentOutcome.Settled => "settled",
        InstantPaymentOutcome.Rejected => "rejected",
        InstantPaymentOutcome.Deferred => "deferred",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    private static string ToTag(TransactionIntakeOutcome outcome) => outcome switch
    {
        TransactionIntakeOutcome.Accepted => "accepted",
        TransactionIntakeOutcome.Replayed => "replayed",
        TransactionIntakeOutcome.InFlight => "in_flight",
        TransactionIntakeOutcome.TransportFailed => "transport_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    private static string ToTag(TransactionProcessedOutcome outcome) => outcome switch
    {
        TransactionProcessedOutcome.Completed => "completed",
        TransactionProcessedOutcome.BusinessRejected => "business_rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    private static string ToTag(StoreKind storeKind) => storeKind switch
    {
        StoreKind.Inbox => "inbox",
        StoreKind.Outbox => "outbox",
        _ => throw new ArgumentOutOfRangeException(nameof(storeKind), storeKind, null)
    };

    private static string ToTag(StoreName storeName) => storeName switch
    {
        StoreName.PaymentsOutbox => "payments-outbox",
        StoreName.CoreBankInbox => "corebank-inbox",
        StoreName.CoreBankOutbox => "corebank-outbox",
        StoreName.PaymentsInbox => "payments-inbox",
        _ => throw new ArgumentOutOfRangeException(nameof(storeName), storeName, null)
    };

    private static string ToTag(StoreOperationOutcome outcome) => outcome switch
    {
        StoreOperationOutcome.Added => "added",
        StoreOperationOutcome.Duplicate => "duplicate",
        StoreOperationOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    private static string ToTag(ItemOutcome outcome) => outcome switch
    {
        ItemOutcome.Completed => "completed",
        ItemOutcome.RetryScheduled => "retry_scheduled",
        ItemOutcome.TerminalFailed => "terminal_failed",
        ItemOutcome.CompletionPersistenceFailed => "completion_persistence_failed",
        ItemOutcome.RetryPersistenceFailed => "retry_persistence_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    private static string ToTag(DeliveryDirection direction) => direction switch
    {
        DeliveryDirection.Sent => "sent",
        DeliveryDirection.Received => "received",
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
    };

    private static string ToTag(Transport transport) => transport switch
    {
        Transport.Http => "http",
        Transport.Dapr => "dapr",
        _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, null)
    };

    private static string ToTag(MessageType messageType) => messageType switch
    {
        MessageType.TransactionCommand => "transaction-command",
        MessageType.TransactionCompleted => "transaction-completed",
        MessageType.TransactionFailed => "transaction-failed",
        MessageType.BalanceUpdated => "balance-updated",
        MessageType.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, null)
    };

    private static string ToTag(DeliveryOutcome outcome) => outcome switch
    {
        DeliveryOutcome.Succeeded => "succeeded",
        DeliveryOutcome.Failed => "failed",
        DeliveryOutcome.Duplicate => "duplicate",
        DeliveryOutcome.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    /// <summary>Disposes the owned <see cref="Meter"/> (and its instruments).</summary>
    public void Dispose() => _meter.Dispose();
}
