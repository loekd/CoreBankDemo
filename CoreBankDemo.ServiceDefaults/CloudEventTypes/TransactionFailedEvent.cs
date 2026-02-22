namespace CoreBankDemo.ServiceDefaults.CloudEventTypes;

public record TransactionFailedEvent(
    string TransactionId,
    string Status,
    DateTimeOffset ProcessedAt,
    string? ErrorReason
);

