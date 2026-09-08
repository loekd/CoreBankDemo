namespace CoreBankDemo.ServiceDefaults.CloudEventTypes;

/// <summary>
/// Payload of <see cref="Constants.TransactionCancelled"/> (spec:
/// instant-rail-cancelled-event). <see cref="Status"/> is always the wire word
/// <c>Cancelled</c>; <see cref="ProcessedAt"/> is the cancellation time CoreBank
/// cached on the row, so a consumer records the same timestamp a <c>504</c>
/// caller was shown; <see cref="Reason"/> is the cancellation reason recorded as
/// the row's <c>LastError</c>, nullable and emitted as JSON <c>null</c> rather
/// than omitted, like <see cref="TransactionFailedEvent.ErrorReason"/>.
/// </summary>
public record TransactionCancelledEvent(
    string TransactionId,
    string Status,
    DateTimeOffset ProcessedAt,
    string? Reason
);
