namespace CoreBankDemo.CoreBankAPI.Models;

public record AccountDetailsResponse(
    string AccountNumber,
    string AccountHolderName,
    decimal Balance,
    string Currency,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt
);
