using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
using Moq;
using Xunit;

namespace CoreBankDemo.CoreBankAPI.Tests;

/// <summary>
/// Tier 1 (Moq against <see cref="IAccountRepository"/>, no real database) —
/// covers every row of spec-4-5's I/O &amp; Edge-Case Matrix for
/// <see cref="AccountQueryHandler.ValidateAsync"/> and
/// <see cref="AccountQueryHandler.GetDetailsAsync"/>.
/// </summary>
public class AccountQueryHandlerTests
{
    private const string AccountNumber = "NL91ABNA0417164300";

    private readonly Mock<IAccountRepository> _repository = new(MockBehavior.Strict);

    private AccountQueryHandler CreateHandler() => new(_repository.Object);

    private static Account NewAccount(bool isActive = true) => new()
    {
        AccountNumber = AccountNumber,
        AccountHolderName = "Test Holder",
        Balance = 123.45m,
        Currency = "EUR",
        IsActive = isActive,
        CreatedAt = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 8, 25, 8, 30, 0, DateTimeKind.Utc)
    };

    [Fact]
    public async Task ValidateAsync_returns_valid_with_holder_and_balance_when_the_account_exists_and_is_active()
    {
        var account = NewAccount(isActive: true);
        _repository.Setup(r => r.FindByAccountNumberAsync(AccountNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        var handler = CreateHandler();

        var result = await handler.ValidateAsync(new AccountValidationRequest(AccountNumber), TestContext.Current.CancellationToken);

        result.Should().Be(new AccountValidationResponse(AccountNumber, true, account.AccountHolderName, account.Balance));

        _repository.Verify(r => r.FindByAccountNumberAsync(AccountNumber, It.IsAny<CancellationToken>()), Times.Once);
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ValidateAsync_returns_invalid_but_still_surfaces_real_holder_and_balance_when_the_account_is_inactive()
    {
        var account = NewAccount(isActive: false);
        _repository.Setup(r => r.FindByAccountNumberAsync(AccountNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        var handler = CreateHandler();

        var result = await handler.ValidateAsync(new AccountValidationRequest(AccountNumber), TestContext.Current.CancellationToken);

        result.Should().Be(new AccountValidationResponse(AccountNumber, false, account.AccountHolderName, account.Balance));
    }

    [Fact]
    public async Task ValidateAsync_returns_invalid_with_null_holder_and_balance_when_the_account_does_not_exist()
    {
        _repository.Setup(r => r.FindByAccountNumberAsync(AccountNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account?)null);

        var handler = CreateHandler();

        var result = await handler.ValidateAsync(new AccountValidationRequest(AccountNumber), TestContext.Current.CancellationToken);

        result.Should().Be(new AccountValidationResponse(AccountNumber, false, null, null));
    }

    [Fact]
    public async Task GetDetailsAsync_returns_found_with_full_details_when_the_account_exists()
    {
        var account = NewAccount(isActive: true);
        _repository.Setup(r => r.FindByAccountNumberAsync(AccountNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        var handler = CreateHandler();

        var result = await handler.GetDetailsAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Found.Should().BeTrue();
        result.Response.Should().Be(new AccountDetailsResponse(
            account.AccountNumber,
            account.AccountHolderName,
            account.Balance,
            account.Currency,
            account.IsActive,
            new DateTimeOffset(account.CreatedAt, TimeSpan.Zero),
            new DateTimeOffset(account.UpdatedAt!.Value, TimeSpan.Zero)));

        _repository.Verify(r => r.FindByAccountNumberAsync(AccountNumber, It.IsAny<CancellationToken>()), Times.Once);
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetDetailsAsync_returns_found_with_a_null_updated_at_when_the_account_was_never_updated()
    {
        var account = NewAccount(isActive: true);
        account.UpdatedAt = null;
        _repository.Setup(r => r.FindByAccountNumberAsync(AccountNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        var handler = CreateHandler();

        var result = await handler.GetDetailsAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Found.Should().BeTrue();
        result.Response!.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public async Task GetDetailsAsync_reports_inactive_accounts_as_found_with_their_real_is_active_value()
    {
        var account = NewAccount(isActive: false);
        _repository.Setup(r => r.FindByAccountNumberAsync(AccountNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        var handler = CreateHandler();

        var result = await handler.GetDetailsAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Found.Should().BeTrue();
        result.Response!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task GetDetailsAsync_returns_not_found_with_a_null_response_when_the_account_does_not_exist()
    {
        _repository.Setup(r => r.FindByAccountNumberAsync(AccountNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account?)null);

        var handler = CreateHandler();

        var result = await handler.GetDetailsAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Found.Should().BeFalse();
        result.Response.Should().BeNull();
    }
}
