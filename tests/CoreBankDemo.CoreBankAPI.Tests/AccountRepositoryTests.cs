using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI.Inbox;
using Xunit;

namespace CoreBankDemo.CoreBankAPI.Tests;

public class AccountRepositoryTests : SqliteCoreBankApiTestBase
{
    [Fact]
    public async Task FindByAccountNumberAsync_returns_the_tracked_account_when_it_exists()
    {
        await using var context = CreateContext();
        var account = NewAccount("NL91ABNA0417164300");
        context.Accounts.Add(account);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new AccountRepository(context);

        var result = await repository.FindByAccountNumberAsync(account.AccountNumber, TestContext.Current.CancellationToken);

        result.Should().BeSameAs(account);
    }

    [Fact]
    public async Task FindByAccountNumberAsync_returns_null_when_the_account_does_not_exist()
    {
        await using var context = CreateContext();
        var repository = new AccountRepository(context);

        var result = await repository.FindByAccountNumberAsync("NL00NONE0000000000", TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    private static Account NewAccount(string accountNumber) => new()
    {
        AccountNumber = accountNumber,
        AccountHolderName = "Test Holder",
        Balance = 100m,
        Currency = "EUR",
        IsActive = true,
        CreatedAt = new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc)
    };
}
