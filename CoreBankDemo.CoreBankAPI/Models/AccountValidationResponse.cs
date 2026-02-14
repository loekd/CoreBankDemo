namespace CoreBankDemo.CoreBankAPI.Models;

public record AccountValidationResponse(
    string AccountNumber,
    bool IsValid,
    string? AccountHolderName = null,
    decimal? Balance = null
);
