using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.CoreBankAPI.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.CoreBankApi;

public class AccountRepositoryTests(PostgresContainerFixture fixture) : CoreBankApiPostgresTestBase(fixture)
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

    [Fact]
    public async Task LockForUpdateAsync_does_not_trigger_the_First_without_OrderBy_warning()
    {
        var ct = TestContext.Current.CancellationToken;
        // The production host logs this warning on every locked read; turning
        // it into an exception here makes the query shape itself the assertion.
        var options = new DbContextOptionsBuilder<CoreBankDbContext>()
            .UseNpgsql(ConnectionString)
            .ConfigureWarnings(warnings => warnings.Throw(CoreEventId.FirstWithoutOrderByAndFilterWarning))
            .Options;
        await using var context = new CoreBankDbContext(options);
        var account = NewAccount("NL91ABNA0417164300");
        context.Accounts.Add(account);
        await context.SaveChangesAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var repository = new AccountRepository(context);

        var result = await repository.LockForUpdateAsync(account.AccountNumber, ct);

        result.Should().BeSameAs(account);
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
