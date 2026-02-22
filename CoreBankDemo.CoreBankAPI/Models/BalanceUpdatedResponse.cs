namespace CoreBankDemo.CoreBankAPI.Models;

public record BalanceUpdatedResponse(
    string TransactionId,
    string AccountNumber,
    decimal Delta,
    decimal NewBalance,
    string Currency);

