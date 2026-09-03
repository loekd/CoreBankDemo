using System.Text.RegularExpressions;

namespace CoreBankDemo.DemoRunner.Application;

public static partial class PaymentInputValidator
{
    public static IReadOnlyList<string> Validate(PaymentSubmission submission)
    {
        var errors = new List<string>();
        ValidateAccount(submission.Request.FromAccount, "From account", errors);
        ValidateAccount(submission.Request.ToAccount, "To account", errors);

        if (string.Equals(submission.Request.FromAccount, submission.Request.ToAccount, StringComparison.Ordinal))
        {
            errors.Add("From account and to account must differ.");
        }

        if (submission.Request.Amount is < 0.01m or > 1_000_000m)
        {
            errors.Add("Amount must be between 0.01 and 1,000,000.");
        }

        if (!CurrencyPattern().IsMatch(submission.Request.Currency ?? string.Empty))
        {
            errors.Add("Currency must be exactly three uppercase letters.");
        }

        if (submission.IdempotencyMode == IdempotencyMode.Supplied)
        {
            if (string.IsNullOrWhiteSpace(submission.IdempotencyKey))
            {
                errors.Add("A supplied idempotency key is required.");
            }
            else if (submission.IdempotencyKey.Length > 100)
            {
                errors.Add("The supplied idempotency key must be at most 100 characters.");
            }
        }

        if (submission.IdempotencyMode == IdempotencyMode.Omitted && submission.IdempotencyKey is not null)
        {
            errors.Add("Omitted idempotency mode must not send a key.");
        }

        return errors;
    }

    private static void ValidateAccount(string? value, string label, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 15 or > 34)
        {
            errors.Add($"{label} must be between 15 and 34 characters.");
        }
    }

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();
}
