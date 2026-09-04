using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.CoreBankApi;

/// <summary>
/// <see cref="AccountRepository.LockForUpdateAsync"/> is a PostgreSQL-only
/// <c>SELECT ... FOR UPDATE</c>. This class proves it through the production
/// query itself, on real competing connections — never through a
/// provider-neutral load that would pass whether or not the row is actually
/// locked (ADR-016).
/// </summary>
public class AccountRowLockTests(PostgresContainerFixture fixture)
    : CoreBankApiPostgresTestBase(fixture)
{
    private const string AccountNumber = "NL91ABNA0417164300";

    [Fact]
    public async Task A_competing_FOR_UPDATE_waits_for_the_holder_to_commit_and_then_observes_the_committed_state()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAccountAsync(100m, ct);

        // Connection A: take the row lock inside an open transaction.
        await using var holderContext = CreateContext();
        await using var holderTransaction = await holderContext.Database.BeginTransactionAsync(ct);
        var holderRepository = new AccountRepository(holderContext);
        var holderAccount = await holderRepository.LockForUpdateAsync(AccountNumber, ct);
        holderAccount!.Balance = 42m;

        // Connection B: request the same row. It must block, not return stale data.
        await using var waiterContext = CreateContext();
        await using var waiterTransaction = await waiterContext.Database.BeginTransactionAsync(ct);
        var waiterRepository = new AccountRepository(waiterContext);
        var waiter = waiterRepository.LockForUpdateAsync(AccountNumber, ct);

        var observedBlocking = await BlockedForAWhileAsync(waiter);

        await holderContext.SaveChangesAsync(ct);
        await holderTransaction.CommitAsync(ct);

        var waited = await waiter.WaitAsync(PostgresContainerFixture.LockWaitTimeout, ct);
        await waiterTransaction.CommitAsync(ct);

        observedBlocking.Should().BeTrue(
            "connection B must wait on connection A's row lock instead of completing immediately");
        waited!.Balance.Should().Be(42m, "connection B must observe the state connection A committed");
    }

    [Fact]
    public async Task A_competing_FOR_UPDATE_waits_for_a_rollback_and_then_observes_the_unchanged_state()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAccountAsync(100m, ct);

        await using var holderContext = CreateContext();
        await using var holderTransaction = await holderContext.Database.BeginTransactionAsync(ct);
        var holderAccount = await new AccountRepository(holderContext).LockForUpdateAsync(AccountNumber, ct);
        holderAccount!.Balance = 7m;
        await holderContext.SaveChangesAsync(ct);

        await using var waiterContext = CreateContext();
        await using var waiterTransaction = await waiterContext.Database.BeginTransactionAsync(ct);
        var waiter = new AccountRepository(waiterContext).LockForUpdateAsync(AccountNumber, ct);

        var observedBlocking = await BlockedForAWhileAsync(waiter);

        await holderTransaction.RollbackAsync(ct);

        var waited = await waiter.WaitAsync(PostgresContainerFixture.LockWaitTimeout, ct);
        await waiterTransaction.CommitAsync(ct);

        observedBlocking.Should().BeTrue();
        waited!.Balance.Should().Be(100m, "the holder rolled back, so its uncommitted write must be invisible");
    }

    [Fact]
    public async Task Two_transfers_on_the_same_account_serialize_through_the_row_lock_and_conserve_the_balance()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAccountAsync(100m, ct);
        await SeedAccountAsync(0m, ct, "NL20INGB0001234567");

        async Task TransferAsync(decimal amount)
        {
            await using var context = CreateContext();
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            var repository = new AccountRepository(context);
            var from = await repository.LockForUpdateAsync(AccountNumber, ct);
            var to = await repository.LockForUpdateAsync("NL20INGB0001234567", ct);
            from!.Balance -= amount;
            to!.Balance += amount;
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        // Without a real row lock these two read-modify-write cycles would lose
        // one another's update; with FOR UPDATE they serialize.
        await Task.WhenAll(TransferAsync(30m), TransferAsync(20m))
            .WaitAsync(PostgresContainerFixture.LockWaitTimeout, ct);

        await using var verification = CreateContext();
        var accounts = await verification.Accounts.AsNoTracking().ToListAsync(ct);
        accounts.Single(a => a.AccountNumber == AccountNumber).Balance.Should().Be(50m);
        accounts.Single(a => a.AccountNumber == "NL20INGB0001234567").Balance.Should().Be(50m);
        accounts.Sum(a => a.Balance).Should().Be(100m);
    }

    [Fact]
    public async Task LockForUpdateAsync_returns_null_when_the_account_does_not_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();

        var result = await new AccountRepository(context).LockForUpdateAsync("NL00NONE0000000000", ct);

        result.Should().BeNull();
    }

    /// <summary>
    /// Bounded observation that <paramref name="contended"/> really is blocked:
    /// a short, fixed window (never an unbounded wait) that the task must not
    /// complete within. The window is a lower bound on how long the waiter is
    /// held up, so the assertion is one-sided and cannot flake on timer
    /// resolution.
    /// </summary>
    private static async Task<bool> BlockedForAWhileAsync(Task contended)
    {
        var completed = await Task.WhenAny(contended, Task.Delay(TimeSpan.FromMilliseconds(500)));
        return completed != contended;
    }

    private async Task SeedAccountAsync(decimal balance, CancellationToken ct, string accountNumber = AccountNumber)
    {
        await using var context = CreateContext();
        context.Accounts.Add(new Account
        {
            AccountNumber = accountNumber,
            AccountHolderName = "Lock Holder",
            Balance = balance,
            Currency = "EUR",
            IsActive = true,
            CreatedAt = TimeProvider.GetUtcNow().UtcDateTime
        });
        await context.SaveChangesAsync(ct);
    }
}
