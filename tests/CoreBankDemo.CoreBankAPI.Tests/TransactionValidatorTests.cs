using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI.Inbox;
using Xunit;

namespace CoreBankDemo.CoreBankAPI.Tests;

/// <summary>
/// <see cref="TransactionValidator"/> (spec-4-2 I/O & Edge-Case Matrix): five
/// fail-fast checks in a fixed priority order — same-account transfer,
/// invalid amount, unknown/inactive source, unknown/inactive destination,
/// insufficient funds — each with an exact failure message, one reason per
/// call, never aggregated.
/// </summary>
public class TransactionValidatorTests
{
    private const string FromAccountNumber = "NL91ABNA0417164300";
    private const string ToAccountNumber = "NL20INGB0001234567";

    private static Account ActiveAccount(string accountNumber, decimal balance) => new()
    {
        AccountNumber = accountNumber,
        AccountHolderName = "Test Holder",
        Balance = balance,
        Currency = "EUR",
        IsActive = true,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static Account InactiveAccount(string accountNumber, decimal balance) => new()
    {
        AccountNumber = accountNumber,
        AccountHolderName = "Test Holder",
        Balance = balance,
        Currency = "EUR",
        IsActive = false,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void Valid_transfer_between_distinct_active_accounts_with_sufficient_funds_succeeds()
    {
        var fromAccount = ActiveAccount(FromAccountNumber, 100m);
        var toAccount = ActiveAccount(ToAccountNumber, 0m);

        var result = TransactionValidator.Validate(FromAccountNumber, ToAccountNumber, 50m, fromAccount, toAccount);

        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Same_account_transfer_fails_even_when_both_accounts_are_otherwise_valid()
    {
        var account = ActiveAccount(FromAccountNumber, 100m);

        var result = TransactionValidator.Validate(FromAccountNumber, FromAccountNumber, 50m, account, account);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("Cannot transfer to the same account");
    }

    [Fact]
    public void Zero_amount_fails_with_invalid_amount_message()
    {
        var fromAccount = ActiveAccount(FromAccountNumber, 100m);
        var toAccount = ActiveAccount(ToAccountNumber, 0m);

        var result = TransactionValidator.Validate(FromAccountNumber, ToAccountNumber, 0m, fromAccount, toAccount);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("Invalid amount: 0. Amount must be greater than zero");
    }

    [Fact]
    public void Negative_amount_fails_with_invalid_amount_message()
    {
        var fromAccount = ActiveAccount(FromAccountNumber, 100m);
        var toAccount = ActiveAccount(ToAccountNumber, 0m);

        var result = TransactionValidator.Validate(FromAccountNumber, ToAccountNumber, -5m, fromAccount, toAccount);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("Invalid amount: -5. Amount must be greater than zero");
    }

    [Fact]
    public void Unknown_source_account_fails_with_source_not_found_message()
    {
        var toAccount = ActiveAccount(ToAccountNumber, 0m);

        var result = TransactionValidator.Validate(FromAccountNumber, ToAccountNumber, 50m, null, toAccount);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be($"Source account {FromAccountNumber} not found or inactive");
    }

    [Fact]
    public void Inactive_source_account_fails_with_source_not_found_message()
    {
        var fromAccount = InactiveAccount(FromAccountNumber, 100m);
        var toAccount = ActiveAccount(ToAccountNumber, 0m);

        var result = TransactionValidator.Validate(FromAccountNumber, ToAccountNumber, 50m, fromAccount, toAccount);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be($"Source account {FromAccountNumber} not found or inactive");
    }

    [Fact]
    public void Unknown_destination_account_fails_with_destination_not_found_message_when_source_is_valid()
    {
        var fromAccount = ActiveAccount(FromAccountNumber, 100m);

        var result = TransactionValidator.Validate(FromAccountNumber, ToAccountNumber, 50m, fromAccount, null);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be($"Destination account {ToAccountNumber} not found or inactive");
    }

    [Fact]
    public void Inactive_destination_account_fails_with_destination_not_found_message_when_source_is_valid()
    {
        var fromAccount = ActiveAccount(FromAccountNumber, 100m);
        var toAccount = InactiveAccount(ToAccountNumber, 0m);

        var result = TransactionValidator.Validate(FromAccountNumber, ToAccountNumber, 50m, fromAccount, toAccount);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be($"Destination account {ToAccountNumber} not found or inactive");
    }

    [Fact]
    public void Insufficient_funds_fails_with_available_and_required_amounts_when_both_accounts_are_valid()
    {
        var fromAccount = ActiveAccount(FromAccountNumber, 10m);
        var toAccount = ActiveAccount(ToAccountNumber, 0m);

        var result = TransactionValidator.Validate(FromAccountNumber, ToAccountNumber, 50m, fromAccount, toAccount);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("Insufficient funds. Available: 10, Required: 50");
    }

    [Fact]
    public void Balance_exactly_equal_to_amount_is_not_insufficient_funds()
    {
        var fromAccount = ActiveAccount(FromAccountNumber, 50m);
        var toAccount = ActiveAccount(ToAccountNumber, 0m);

        var result = TransactionValidator.Validate(FromAccountNumber, ToAccountNumber, 50m, fromAccount, toAccount);

        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Check_ordering_same_account_wins_over_invalid_amount()
    {
        // Same account number AND a non-positive amount: the same-account
        // check must fire first, not the invalid-amount check.
        var result = TransactionValidator.Validate(FromAccountNumber, FromAccountNumber, 0m, null, null);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("Cannot transfer to the same account");
    }

    [Fact]
    public void Check_ordering_invalid_amount_wins_over_unknown_account()
    {
        // Non-positive amount AND an unknown source account: the amount
        // check must fire first, not the account-existence check.
        var result = TransactionValidator.Validate(FromAccountNumber, ToAccountNumber, 0m, null, null);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("Invalid amount: 0. Amount must be greater than zero");
    }

    [Fact]
    public void Check_ordering_source_unknown_wins_over_destination_unknown()
    {
        // Both accounts unknown: the source check must fire first, not the
        // destination check.
        var result = TransactionValidator.Validate(FromAccountNumber, ToAccountNumber, 50m, null, null);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be($"Source account {FromAccountNumber} not found or inactive");
    }
}
