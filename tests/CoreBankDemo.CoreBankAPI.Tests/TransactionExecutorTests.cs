using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.Messaging;
using Moq;
using Xunit;

namespace CoreBankDemo.CoreBankAPI.Tests;

public class TransactionExecutorTests
{
    private const string FromAccountNumber = "NL91ABNA0417164300";
    private const string ToAccountNumber = "NL20INGB0001234567";
    private const string TransactionId = "txn-123";

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

        var executor = new TransactionExecutor(repository.Object, timeProvider);

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

        var executor = new TransactionExecutor(repository.Object, timeProvider);

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

        var executor = new TransactionExecutor(repository.Object, timeProvider);

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

        var executor = new TransactionExecutor(repository.Object, timeProvider);

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
}
