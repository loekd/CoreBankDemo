namespace CoreBankDemo.PaymentsAPI.Models;

public record TransactionResponse(
    string TransactionId,
    string Status,
    DateTimeOffset ProcessedAt
);
