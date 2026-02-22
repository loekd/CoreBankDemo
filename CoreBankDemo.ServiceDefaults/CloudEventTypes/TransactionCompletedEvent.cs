namespace CoreBankDemo.ServiceDefaults.CloudEventTypes;

public record TransactionCompletedEvent(
    string TransactionId,
    string Status,
    DateTimeOffset ProcessedAt
);

