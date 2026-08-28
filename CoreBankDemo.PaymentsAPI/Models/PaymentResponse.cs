namespace CoreBankDemo.PaymentsAPI.Models;

public record PaymentResponse(
    string PaymentId,
    string TransactionId,
    string Status,
    decimal Amount,
    string Currency,
    DateTimeOffset ProcessedAt
);
