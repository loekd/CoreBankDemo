namespace CoreBankDemo.ServiceDefaults.CloudEventTypes;

public record BalanceUpdatedEvent(
    string TransactionId,
    string AccountNumber,
    decimal Delta,
    decimal NewBalance,
    string Currency
);

