namespace CoreBankDemo.CoreBankAPI.Inbox;

/// <summary>
/// Pure, dependency-free transaction validation (AD-2). Runs five checks in
/// a fixed fail-fast order, returning the first violation found — never
/// aggregated. Takes primitive parameters plus nullable <see cref="Account"/>
/// snapshots so it stays usable from any future caller without a forced
/// dependency on the inbox message shape.
/// </summary>
public static class TransactionValidator
{
    public static ValidationResult Validate(
        string fromAccountNumber,
        string toAccountNumber,
        decimal amount,
        Account? fromAccount,
        Account? toAccount)
    {
        if (string.Equals(fromAccountNumber, toAccountNumber, StringComparison.Ordinal))
            return ValidationResult.Failure("Cannot transfer to the same account");

        if (amount <= 0)
            return ValidationResult.Failure($"Invalid amount: {amount}. Amount must be greater than zero");

        if (fromAccount is null || !fromAccount.IsActive)
            return ValidationResult.Failure($"Source account {fromAccountNumber} not found or inactive");

        if (toAccount is null || !toAccount.IsActive)
            return ValidationResult.Failure($"Destination account {toAccountNumber} not found or inactive");

        if (fromAccount.Balance < amount)
            return ValidationResult.Failure($"Insufficient funds. Available: {fromAccount.Balance}, Required: {amount}");

        return ValidationResult.Success();
    }
}

public record ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Success() => new(true, null);
    public static ValidationResult Failure(string error) => new(false, error);
}
