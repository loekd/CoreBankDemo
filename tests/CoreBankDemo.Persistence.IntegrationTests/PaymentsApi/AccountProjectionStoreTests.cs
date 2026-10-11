using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Accounts;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

/// <summary>
/// The inbox side of the account projection (spec: payments-account-projection)
/// against real PostgreSQL: upsert-if-missing, row locking, settle, release
/// and the clamp at zero.
/// </summary>
public class AccountProjectionStoreTests(PostgresContainerFixture fixture) : PaymentsPostgresTestBase(fixture)
{
    private const string Account = "NL91ABNA0417164300";

    [Fact]
    public async Task SettleAsync_creates_the_row_and_records_the_settled_balance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var store = new AccountProjectionStore(context, TimeProvider);

        await using (var transaction = await context.Database.BeginTransactionAsync(ct))
        {
            await store.SettleAsync(Account, 987.66m, "EUR", ct);
            await transaction.CommitAsync(ct);
        }

        await using var verification = CreateContext();
        var row = await verification.ProjectedAccounts.SingleAsync(ct);
        row.AccountNumber.Should().Be(Account);
        row.SettledBalance.Should().Be(987.66m);
        row.Reserved.Should().Be(0m);
        row.Currency.Should().Be("EUR");
        row.UpdatedAt.Should().Be(TimeProvider.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task SettleAsync_overwrites_an_existing_settled_balance_and_keeps_the_reservation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var seed = CreateContext())
        {
            seed.ProjectedAccounts.Add(new ProjectedAccount
            {
                AccountNumber = Account, SettledBalance = 100m, Reserved = 25m, Currency = "EUR",
                UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            await seed.SaveChangesAsync(ct);
        }
        await using var context = CreateContext();
        var store = new AccountProjectionStore(context, TimeProvider);

        await using (var transaction = await context.Database.BeginTransactionAsync(ct))
        {
            await store.SettleAsync(Account, 75m, "EUR", ct);
            await transaction.CommitAsync(ct);
        }

        await using var verification = CreateContext();
        var row = await verification.ProjectedAccounts.SingleAsync(ct);
        row.SettledBalance.Should().Be(75m);
        row.Reserved.Should().Be(25m, "settling never touches the reservation");
    }

    [Fact]
    public async Task ReleaseAsync_subtracts_the_amount_and_reports_no_shortfall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var seed = CreateContext())
        {
            seed.ProjectedAccounts.Add(new ProjectedAccount
            {
                AccountNumber = Account, SettledBalance = 100m, Reserved = 25m,
                UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            await seed.SaveChangesAsync(ct);
        }
        await using var context = CreateContext();
        var store = new AccountProjectionStore(context, TimeProvider);

        decimal shortfall;
        await using (var transaction = await context.Database.BeginTransactionAsync(ct))
        {
            shortfall = await store.ReleaseAsync(Account, 10m, ct);
            await transaction.CommitAsync(ct);
        }

        shortfall.Should().Be(0m);
        await using var verification = CreateContext();
        (await verification.ProjectedAccounts.SingleAsync(ct)).Reserved.Should().Be(15m);
    }

    [Fact]
    public async Task ReleaseAsync_on_an_unseen_account_creates_the_row_clamps_at_zero_and_reports_the_shortfall()
    {
        // A projection created after the payment was accepted (recreated
        // database) sees a release it never reserved for.
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var store = new AccountProjectionStore(context, TimeProvider);

        decimal shortfall;
        await using (var transaction = await context.Database.BeginTransactionAsync(ct))
        {
            shortfall = await store.ReleaseAsync(Account, 12.34m, ct);
            await transaction.CommitAsync(ct);
        }

        shortfall.Should().Be(12.34m);
        await using var verification = CreateContext();
        var row = await verification.ProjectedAccounts.SingleAsync(ct);
        row.Reserved.Should().Be(0m);
        row.SettledBalance.Should().BeNull();
    }

    [Fact]
    public async Task LockAsync_blocks_a_second_writer_until_the_first_commits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = CreateStore();
        var (first, second) = store.CreateCompetingContexts();
        await using var firstHandle = first;
        await using var secondHandle = second;
        var now = TimeProvider.GetUtcNow().UtcDateTime;

        await using var firstTransaction = await first.Database.BeginTransactionAsync(ct);
        await ProjectedAccountRows.LockAsync(first, Account, now, ct);

        await using var secondTransaction = await second.Database.BeginTransactionAsync(ct);
        var secondLock = ProjectedAccountRows.LockAsync(second, Account, now, ct);
        var completedEarly = await Task.WhenAny(secondLock, Task.Delay(TimeSpan.FromMilliseconds(500), ct));
        completedEarly.Should().NotBeSameAs(secondLock, "the row lock must hold the second writer");

        await firstTransaction.CommitAsync(ct);
        (await secondLock.WaitAsync(PostgresContainerFixture.LockWaitTimeout, ct)).AccountNumber.Should().Be(Account);
        await secondTransaction.CommitAsync(ct);

        await using var verification = CreateContext();
        (await verification.ProjectedAccounts.CountAsync(ct)).Should().Be(1, "ON CONFLICT DO NOTHING creates the row once");
    }

    [Fact]
    public async Task ReleaseAsync_retried_on_the_same_context_after_a_rollback_releases_from_the_database_value()
    {
        // An execution-strategy retry re-runs the inbox transaction on the same
        // context: the first attempt's release was accepted into the tracker,
        // then rolled back in the database.
        var ct = TestContext.Current.CancellationToken;
        await using (var seed = CreateContext())
        {
            seed.ProjectedAccounts.Add(new ProjectedAccount
            {
                AccountNumber = Account, SettledBalance = 100m, Reserved = 12.34m,
                UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            await seed.SaveChangesAsync(ct);
        }
        await using var context = CreateContext();
        var store = new AccountProjectionStore(context, TimeProvider);
        await using (var attempt1 = await context.Database.BeginTransactionAsync(ct))
        {
            (await store.ReleaseAsync(Account, 12.34m, ct)).Should().Be(0m);
            await attempt1.RollbackAsync(ct);
        }

        decimal shortfall;
        await using (var attempt2 = await context.Database.BeginTransactionAsync(ct))
        {
            shortfall = await store.ReleaseAsync(Account, 12.34m, ct);
            await attempt2.CommitAsync(ct);
        }

        shortfall.Should().Be(0m, "the retry must see the rolled-back database value, not the stale tracked one");
        await using var verification = CreateContext();
        (await verification.ProjectedAccounts.SingleAsync(ct)).Reserved.Should().Be(0m);
    }

    [Fact]
    public async Task SettleAsync_retried_on_the_same_context_after_a_rollback_commits_the_retried_value()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var seed = CreateContext())
        {
            seed.ProjectedAccounts.Add(new ProjectedAccount
            {
                AccountNumber = Account, SettledBalance = 100m, Reserved = 0m, Currency = "EUR",
                UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            await seed.SaveChangesAsync(ct);
        }
        await using var context = CreateContext();
        var store = new AccountProjectionStore(context, TimeProvider);
        await using (var attempt1 = await context.Database.BeginTransactionAsync(ct))
        {
            await store.SettleAsync(Account, 50m, "EUR", ct);
            await attempt1.RollbackAsync(ct);
        }

        await using (var attempt2 = await context.Database.BeginTransactionAsync(ct))
        {
            await store.SettleAsync(Account, 75m, "EUR", ct);
            await attempt2.CommitAsync(ct);
        }

        await using var verification = CreateContext();
        (await verification.ProjectedAccounts.SingleAsync(ct)).SettledBalance.Should().Be(75m);
    }
}
