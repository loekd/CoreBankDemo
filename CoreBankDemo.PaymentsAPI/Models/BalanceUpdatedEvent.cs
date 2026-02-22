namespace CoreBankDemo.PaymentsAPI.Models;

public record BalanceUpdatedEvent(
    string TransactionId,
    string AccountNumber,
    decimal Delta,
    decimal NewBalance,
    string Currency
);
