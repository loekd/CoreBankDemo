namespace CoreBankDemo.PaymentsAPI.Models;

public record TransactionCompletedEvent(
    string TransactionId,
    string Status,
    DateTimeOffset ProcessedAt
);

