using CoreBankDemo.CoreBankAPI.Models;

namespace CoreBankDemo.CoreBankAPI.Inbox;

public class TransactionValidator
{
    public ValidationResult ValidateTransaction(
        InboxMessage message,
        Account? fromAccount,
        Account? toAccount)
    {
        if (fromAccount == null || !fromAccount.IsActive)
            return ValidationResult.Failure($"Source account {message.FromAccount} not found or inactive");

        if (toAccount == null || !toAccount.IsActive)
            return ValidationResult.Failure($"Destination account {message.ToAccount} not found or inactive");

        if (fromAccount.Balance < message.Amount)
            return ValidationResult.Failure($"Insufficient funds. Available: {fromAccount.Balance}, Required: {message.Amount}");

        return ValidationResult.Success();
    }
}

public record ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Success() => new(true, null);
    public static ValidationResult Failure(string error) => new(false, error);
}
