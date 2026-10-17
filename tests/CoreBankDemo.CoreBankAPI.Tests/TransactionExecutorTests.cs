using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.Messaging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CoreBankDemo.CoreBankAPI.Tests;

public class TransactionExecutorTests
{
    private const string FromAccountNumber = "NL91ABNA0417164300";
    private const string ToAccountNumber = "NL20INGB0001234567";
    private const string TransactionId = "txn-123";
    private static readonly IOptions<DemoOptions> RowLocking = Options.Create(new DemoOptions());
    private static readonly IOptions<DemoOptions> PartitionLocking =
        Options.Create(new DemoOptions { LockStrategy = LockStrategy.Partition });
    private static readonly IOptions<DemoOptions> RowLockingHeldUntilPublished =
        Options.Create(new DemoOptions { LockRelease = LockRelease.AfterPublish });
    private static readonly IOptions<DemoOptions> PartitionLockingHeldUntilPublished =
        Options.Create(new DemoOptions { LockStrategy = LockStrategy.Partition, LockRelease = LockRelease.AfterPublish });

    public static TheoryData<TransactionFailureScenario> FailureScenarios => new()
    {
        new TransactionFailureScenario(
            "Unknown source account",
            FromAccountNumber,
            ToAccountNumber,
            50m,
            null,
            ActiveAccount(ToAccountNumber, 25m),
            $"Source account {FromAccountNumber} not found or inactive"),
        new TransactionFailureScenario(
            "Inactive source account",
            FromAccountNumber,
            ToAccountNumber,
            50m,
            InactiveAccount(FromAccountNumber, 100m),
            ActiveAccount(ToAccountNumber, 25m),
            $"Source account {FromAccountNumber} not found or inactive"),
        new TransactionFailureScenario(
            "Unknown destination account",
            FromAccountNumber,
            ToAccountNumber,
            50m,
            ActiveAccount(FromAccountNumber, 100m),
            null,
            $"Destination account {ToAccountNumber} not found or inactive"),
        new TransactionFailureScenario(
            "Inactive destination account",
            FromAccountNumber,
            ToAccountNumber,
            50m,
            ActiveAccount(FromAccountNumber, 100m),
            InactiveAccount(ToAccountNumber, 25m),
            $"Destination account {ToAccountNumber} not found or inactive"),
        new TransactionFailureScenario(
            "Invalid amount",
            FromAccountNumber,
            ToAccountNumber,
            0m,
            ActiveAccount(FromAccountNumber, 100m),
            ActiveAccount(ToAccountNumber, 25m),
            "Invalid amount: 0. Amount must be greater than zero"),
        new TransactionFailureScenario(
            "Insufficient funds",
            FromAccountNumber,
            ToAccountNumber,
            150m,
            ActiveAccount(FromAccountNumber, 100m),
            ActiveAccount(ToAccountNumber, 25m),
            "Insufficient funds. Available: 100, Required: 150")
    };

    [Fact]
    public async Task ExecuteAsync_locks_from_then_to_when_from_account_number_is_alphabetically_first_and_applies_the_transfer()
    {
        var timeProvider = new FakeTimeProvider();
        const string fromAccountNumber = "AAA-ACCOUNT";
        const string toAccountNumber = "ZZZ-ACCOUNT";
        var fromAccount = ActiveAccount(fromAccountNumber, 100m);
        var toAccount = ActiveAccount(toAccountNumber, 25m);
        var repository = new Mock<IAccountRepository>(MockBehavior.Strict);
        var callOrder = new List<string>();
        var sequence = new MockSequence();

        repository.InSequence(sequence)
            .Setup(r => r.LockForUpdateAsync(fromAccountNumber, It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((accountNumber, _) => callOrder.Add(accountNumber))
            .ReturnsAsync(fromAccount);
        repository.InSequence(sequence)
            .Setup(r => r.LockForUpdateAsync(toAccountNumber, It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((accountNumber, _) => callOrder.Add(accountNumber))
            .ReturnsAsync(toAccount);

        var executor = new TransactionExecutor(repository.Object, timeProvider, RowLocking);

        var result = await executor.ExecuteAsync(
            fromAccountNumber,
            toAccountNumber,
            50m,
            TransactionId,
            TestContext.Current.CancellationToken);

        callOrder.Should().Equal(fromAccountNumber, toAccountNumber);
        result.Success.Should().BeTrue();
        result.ErrorReason.Should().BeNull();
        result.NewFromBalance.Should().Be(50m);
        result.NewToBalance.Should().Be(75m);
        result.Response.Should().Be(new CoreBankDemo.CoreBankAPI.Models.TransactionResponse(
            TransactionId,
            MessageConstants.Status.Completed,
            timeProvider.GetUtcNow()));
        fromAccount.Balance.Should().Be(50m);
        toAccount.Balance.Should().Be(75m);
        fromAccount.UpdatedAt.Should().Be(timeProvider.GetUtcNow().UtcDateTime);
        toAccount.UpdatedAt.Should().Be(timeProvider.GetUtcNow().UtcDateTime);
        repository.Verify(r => r.LockForUpdateAsync(fromAccountNumber, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.LockForUpdateAsync(toAccountNumber, It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_locks_to_then_from_when_to_account_number_is_alphabetically_first_and_applies_the_transfer()
    {
        var timeProvider = new FakeTimeProvider();
        const string fromAccountNumber = "ZZZ-ACCOUNT";
        const string toAccountNumber = "AAA-ACCOUNT";
        var fromAccount = ActiveAccount(fromAccountNumber, 125m);
        var toAccount = ActiveAccount(toAccountNumber, 10m);
        var repository = new Mock<IAccountRepository>(MockBehavior.Strict);
        var callOrder = new List<string>();
        var sequence = new MockSequence();

        repository.InSequence(sequence)
            .Setup(r => r.LockForUpdateAsync(toAccountNumber, It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((accountNumber, _) => callOrder.Add(accountNumber))
            .ReturnsAsync(toAccount);
        repository.InSequence(sequence)
            .Setup(r => r.LockForUpdateAsync(fromAccountNumber, It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((accountNumber, _) => callOrder.Add(accountNumber))
            .ReturnsAsync(fromAccount);

        var executor = new TransactionExecutor(repository.Object, timeProvider, RowLocking);

        var result = await executor.ExecuteAsync(
            fromAccountNumber,
            toAccountNumber,
            25m,
            TransactionId,
            TestContext.Current.CancellationToken);

        callOrder.Should().Equal(toAccountNumber, fromAccountNumber);
        result.Success.Should().BeTrue();
        result.ErrorReason.Should().BeNull();
        result.NewFromBalance.Should().Be(100m);
        result.NewToBalance.Should().Be(35m);
        result.Response.Status.Should().Be(MessageConstants.Status.Completed);
        fromAccount.Balance.Should().Be(100m);
        toAccount.Balance.Should().Be(35m);
        fromAccount.UpdatedAt.Should().Be(timeProvider.GetUtcNow().UtcDateTime);
        toAccount.UpdatedAt.Should().Be(timeProvider.GetUtcNow().UtcDateTime);
        repository.Verify(r => r.LockForUpdateAsync(toAccountNumber, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.LockForUpdateAsync(fromAccountNumber, It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_locks_a_same_account_transfer_once_and_returns_the_validator_failure_without_mutating_balances()
    {
        var timeProvider = new FakeTimeProvider();
        var account = ActiveAccount(FromAccountNumber, 100m);
        var repository = new Mock<IAccountRepository>(MockBehavior.Strict);

        repository.Setup(r => r.LockForUpdateAsync(FromAccountNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        var executor = new TransactionExecutor(repository.Object, timeProvider, RowLocking);

        var result = await executor.ExecuteAsync(
            FromAccountNumber,
            FromAccountNumber,
            50m,
            TransactionId,
            TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be("Cannot transfer to the same account");
        result.NewFromBalance.Should().BeNull();
        result.NewToBalance.Should().BeNull();
        result.Response.Should().Be(new CoreBankDemo.CoreBankAPI.Models.TransactionResponse(
            TransactionId,
            MessageConstants.Status.Failed,
            timeProvider.GetUtcNow()));
        account.Balance.Should().Be(100m);
        account.UpdatedAt.Should().BeNull();
        repository.Verify(r => r.LockForUpdateAsync(FromAccountNumber, It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(FailureScenarios))]
    public async Task ExecuteAsync_returns_failed_response_for_validator_failures_without_mutating_balances(TransactionFailureScenario scenario)
    {
        var timeProvider = new FakeTimeProvider();
        var repository = new Mock<IAccountRepository>(MockBehavior.Strict);
        var callOrder = new List<string>();
        var sequence = new MockSequence();
        var expectedLockOrder = string.CompareOrdinal(scenario.FromAccountNumber, scenario.ToAccountNumber) < 0
            ? new[] { scenario.FromAccountNumber, scenario.ToAccountNumber }
            : new[] { scenario.ToAccountNumber, scenario.FromAccountNumber };

        repository.InSequence(sequence)
            .Setup(r => r.LockForUpdateAsync(expectedLockOrder[0], It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((accountNumber, _) => callOrder.Add(accountNumber))
            .ReturnsAsync(expectedLockOrder[0] == scenario.FromAccountNumber ? scenario.FromAccount : scenario.ToAccount);
        repository.InSequence(sequence)
            .Setup(r => r.LockForUpdateAsync(expectedLockOrder[1], It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((accountNumber, _) => callOrder.Add(accountNumber))
            .ReturnsAsync(expectedLockOrder[1] == scenario.FromAccountNumber ? scenario.FromAccount : scenario.ToAccount);

        var fromStartingBalance = scenario.FromAccount?.Balance;
        var toStartingBalance = scenario.ToAccount?.Balance;

        var executor = new TransactionExecutor(repository.Object, timeProvider, RowLocking);

        var result = await executor.ExecuteAsync(
            scenario.FromAccountNumber,
            scenario.ToAccountNumber,
            scenario.Amount,
            TransactionId,
            TestContext.Current.CancellationToken);

        callOrder.Should().Equal(expectedLockOrder);
        result.Success.Should().BeFalse(scenario.Name);
        result.ErrorReason.Should().Be(scenario.ExpectedError);
        result.NewFromBalance.Should().BeNull();
        result.NewToBalance.Should().BeNull();
        result.Response.Should().Be(new CoreBankDemo.CoreBankAPI.Models.TransactionResponse(
            TransactionId,
            MessageConstants.Status.Failed,
            timeProvider.GetUtcNow()));
        scenario.FromAccount?.Balance.Should().Be(fromStartingBalance);
        scenario.ToAccount?.Balance.Should().Be(toStartingBalance);
        scenario.FromAccount?.UpdatedAt.Should().BeNull();
        scenario.ToAccount?.UpdatedAt.Should().BeNull();
        repository.Verify(r => r.LockForUpdateAsync(expectedLockOrder[0], It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.LockForUpdateAsync(expectedLockOrder[1], It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public void DemoOptions_default_to_row_locking()
    {
        new DemoOptions().LockStrategy.Should().Be(LockStrategy.Row);
    }

    [Fact]
    public async Task Partition_strategy_locks_distinct_partitions_ascending_then_loads_both_accounts_without_row_locks()
    {
        var timeProvider = new FakeTimeProvider();
        // ACC-B hashes to partition 2, ACC-C to partition 1 (PartitionHelper, 4 partitions):
        // the from-account's partition is the higher id, so ascending order is [1, 2].
        const string fromAccountNumber = "ACC-B";
        const string toAccountNumber = "ACC-C";
        PartitionHelper.GetPartitionId(fromAccountNumber, 4).Should().Be(2);
        PartitionHelper.GetPartitionId(toAccountNumber, 4).Should().Be(1);
        var fromAccount = ActiveAccount(fromAccountNumber, 100m);
        var toAccount = ActiveAccount(toAccountNumber, 25m);
        var repository = new Mock<IAccountRepository>(MockBehavior.Strict);
        var callOrder = new List<string>();

        repository.Setup(r => r.LockPartitionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<int, CancellationToken>((partitionId, _) => callOrder.Add($"lock:{partitionId}"))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.FindByAccountNumberAsync(fromAccountNumber, It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((accountNumber, _) => callOrder.Add($"find:{accountNumber}"))
            .ReturnsAsync(fromAccount);
        repository.Setup(r => r.FindByAccountNumberAsync(toAccountNumber, It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((accountNumber, _) => callOrder.Add($"find:{accountNumber}"))
            .ReturnsAsync(toAccount);

        var executor = new TransactionExecutor(repository.Object, timeProvider, PartitionLocking);

        var result = await executor.ExecuteAsync(
            fromAccountNumber,
            toAccountNumber,
            50m,
            TransactionId,
            TestContext.Current.CancellationToken);

        callOrder.Should().Equal("lock:1", "lock:2", $"find:{fromAccountNumber}", $"find:{toAccountNumber}");
        result.Success.Should().BeTrue();
        result.NewFromBalance.Should().Be(50m);
        result.NewToBalance.Should().Be(75m);
        fromAccount.Balance.Should().Be(50m);
        toAccount.Balance.Should().Be(75m);
        repository.Verify(r => r.LockPartitionAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.LockPartitionAsync(2, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.FindByAccountNumberAsync(fromAccountNumber, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.FindByAccountNumberAsync(toAccountNumber, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.LockForUpdateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Partition_strategy_locks_a_shared_partition_once_for_two_accounts_in_the_same_partition()
    {
        var timeProvider = new FakeTimeProvider();
        // ACC-A and ACC-E both hash to partition 0.
        const string fromAccountNumber = "ACC-A";
        const string toAccountNumber = "ACC-E";
        PartitionHelper.GetPartitionId(fromAccountNumber, 4).Should().Be(0);
        PartitionHelper.GetPartitionId(toAccountNumber, 4).Should().Be(0);
        var fromAccount = ActiveAccount(fromAccountNumber, 10m);
        var toAccount = ActiveAccount(toAccountNumber, 0m);
        var repository = new Mock<IAccountRepository>(MockBehavior.Strict);

        repository.Setup(r => r.LockPartitionAsync(0, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        repository.Setup(r => r.FindByAccountNumberAsync(fromAccountNumber, It.IsAny<CancellationToken>())).ReturnsAsync(fromAccount);
        repository.Setup(r => r.FindByAccountNumberAsync(toAccountNumber, It.IsAny<CancellationToken>())).ReturnsAsync(toAccount);

        var executor = new TransactionExecutor(repository.Object, timeProvider, PartitionLocking);

        var result = await executor.ExecuteAsync(
            fromAccountNumber,
            toAccountNumber,
            10m,
            TransactionId,
            TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.NewFromBalance.Should().Be(0m);
        result.NewToBalance.Should().Be(10m);
        repository.Verify(r => r.LockPartitionAsync(0, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.LockPartitionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.FindByAccountNumberAsync(fromAccountNumber, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.FindByAccountNumberAsync(toAccountNumber, It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Partition_strategy_locks_once_and_loads_once_for_a_same_account_transfer_and_returns_the_validator_failure()
    {
        var timeProvider = new FakeTimeProvider();
        var expectedPartitionId = PartitionHelper.GetPartitionId(FromAccountNumber, 4);
        var account = ActiveAccount(FromAccountNumber, 100m);
        var repository = new Mock<IAccountRepository>(MockBehavior.Strict);

        repository.Setup(r => r.LockPartitionAsync(expectedPartitionId, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        repository.Setup(r => r.FindByAccountNumberAsync(FromAccountNumber, It.IsAny<CancellationToken>())).ReturnsAsync(account);

        var executor = new TransactionExecutor(repository.Object, timeProvider, PartitionLocking);

        var result = await executor.ExecuteAsync(
            FromAccountNumber,
            FromAccountNumber,
            50m,
            TransactionId,
            TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be("Cannot transfer to the same account");
        account.Balance.Should().Be(100m);
        repository.Verify(r => r.LockPartitionAsync(expectedPartitionId, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.FindByAccountNumberAsync(FromAccountNumber, It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Partition_strategy_returns_failed_response_when_the_source_account_is_unknown()
    {
        var timeProvider = new FakeTimeProvider();
        var toAccount = ActiveAccount(ToAccountNumber, 25m);
        var repository = new Mock<IAccountRepository>(MockBehavior.Strict);

        repository.Setup(r => r.LockPartitionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        repository.Setup(r => r.FindByAccountNumberAsync(FromAccountNumber, It.IsAny<CancellationToken>())).ReturnsAsync((Account?)null);
        repository.Setup(r => r.FindByAccountNumberAsync(ToAccountNumber, It.IsAny<CancellationToken>())).ReturnsAsync(toAccount);

        var executor = new TransactionExecutor(repository.Object, timeProvider, PartitionLocking);

        var result = await executor.ExecuteAsync(
            FromAccountNumber,
            ToAccountNumber,
            50m,
            TransactionId,
            TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be($"Source account {FromAccountNumber} not found or inactive");
        result.Response.Status.Should().Be(MessageConstants.Status.Failed);
        toAccount.Balance.Should().Be(25m);
        repository.Verify(r => r.LockPartitionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

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

    public sealed record TransactionFailureScenario(
        string Name,
        string FromAccountNumber,
        string ToAccountNumber,
        decimal Amount,
        Account? FromAccount,
        Account? ToAccount,
        string ExpectedError)
    {
        public override string ToString() => Name;
    }

    [Fact]
    public async Task AfterPublish_release_under_row_strategy_holds_both_accounts_ascending_before_the_row_locks()
    {
        // The hold order equals the FOR UPDATE order (ordinal), so a holder never waits on
        // a key another holder takes first. ToAccountNumber sorts before FromAccountNumber.
        var calls = new List<string>();
        var repository = new Mock<IAccountRepository>(MockBehavior.Strict);
        repository.Setup(r => r.HoldAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((account, _) => calls.Add($"hold:{account}")).Returns(Task.CompletedTask);
        repository.Setup(r => r.LockForUpdateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((account, _) => calls.Add($"row:{account}"))
            .ReturnsAsync((string account, CancellationToken _) => ActiveAccount(account, 100m));

        var executor = new TransactionExecutor(repository.Object, new FakeTimeProvider(), RowLockingHeldUntilPublished);
        var result = await executor.ExecuteAsync(FromAccountNumber, ToAccountNumber, 50m, TransactionId, CancellationToken.None);

        result.Success.Should().BeTrue();
        calls.Should().Equal(
            $"hold:{ToAccountNumber}", $"hold:{FromAccountNumber}",
            $"row:{ToAccountNumber}", $"row:{FromAccountNumber}");
        repository.Verify(r => r.HoldPartitionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AfterPublish_release_under_row_strategy_holds_a_same_account_transfer_once()
    {
        var repository = new Mock<IAccountRepository>(MockBehavior.Strict);
        repository.Setup(r => r.HoldAccountAsync(FromAccountNumber, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        repository.Setup(r => r.LockForUpdateAsync(FromAccountNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveAccount(FromAccountNumber, 100m));

        var executor = new TransactionExecutor(repository.Object, new FakeTimeProvider(), RowLockingHeldUntilPublished);
        await executor.ExecuteAsync(FromAccountNumber, FromAccountNumber, 50m, TransactionId, CancellationToken.None);

        repository.Verify(r => r.HoldAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AfterPublish_release_under_partition_strategy_holds_the_distinct_partitions_ascending_before_the_partition_locks()
    {
        var fromAccountNumber = "ACC-B";
        var toAccountNumber = "ACC-C";
        PartitionHelper.GetPartitionId(fromAccountNumber, 4).Should().Be(2);
        PartitionHelper.GetPartitionId(toAccountNumber, 4).Should().Be(1);
        var calls = new List<string>();
        var repository = new Mock<IAccountRepository>(MockBehavior.Strict);
        repository.Setup(r => r.HoldPartitionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<int, CancellationToken>((id, _) => calls.Add($"hold:{id}")).Returns(Task.CompletedTask);
        repository.Setup(r => r.LockPartitionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<int, CancellationToken>((id, _) => calls.Add($"lock:{id}")).Returns(Task.CompletedTask);
        repository.Setup(r => r.FindByAccountNumberAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string account, CancellationToken _) => ActiveAccount(account, 100m));

        var executor = new TransactionExecutor(repository.Object, new FakeTimeProvider(), PartitionLockingHeldUntilPublished);
        var result = await executor.ExecuteAsync(fromAccountNumber, toAccountNumber, 50m, TransactionId, CancellationToken.None);

        result.Success.Should().BeTrue();
        calls.Should().Equal("hold:1", "hold:2", "lock:1", "lock:2");
        repository.Verify(r => r.HoldAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Commit_release_takes_no_session_holds_under_either_strategy()
    {
        foreach (var options in new[] { RowLocking, PartitionLocking })
        {
            var repository = new Mock<IAccountRepository>();
            repository.Setup(r => r.LockForUpdateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string account, CancellationToken _) => ActiveAccount(account, 100m));
            repository.Setup(r => r.FindByAccountNumberAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string account, CancellationToken _) => ActiveAccount(account, 100m));

            await new TransactionExecutor(repository.Object, new FakeTimeProvider(), options)
                .ExecuteAsync(FromAccountNumber, ToAccountNumber, 50m, TransactionId, CancellationToken.None);

            repository.Verify(r => r.HoldAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            repository.Verify(r => r.HoldPartitionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
