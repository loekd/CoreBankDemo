using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.Messaging;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.CoreBankAPI.Inbox;

internal interface ITransactionExecutor
{
    Task<TransactionExecutionResult> ExecuteAsync(
        string fromAccountNumber,
        string toAccountNumber,
        decimal amount,
        string transactionId,
        CancellationToken cancellationToken);
}

internal sealed class TransactionExecutor(
    IAccountRepository accountRepository,
    TimeProvider timeProvider,
    IOptions<DemoOptions> demoOptions) : ITransactionExecutor
{
    private const int PartitionCount = 4;
    private readonly LockStrategy lockStrategy = demoOptions.Value.LockStrategy;
    private readonly LockRelease lockRelease = demoOptions.Value.LockRelease;

    public async Task<TransactionExecutionResult> ExecuteAsync(
        string fromAccountNumber,
        string toAccountNumber,
        decimal amount,
        string transactionId,
        CancellationToken cancellationToken)
    {
        var (fromAccount, toAccount) = await LoadAccountsAsync(
            fromAccountNumber,
            toAccountNumber,
            cancellationToken);

        var validationResult = TransactionValidator.Validate(
            fromAccountNumber,
            toAccountNumber,
            amount,
            fromAccount,
            toAccount);

        var processedAt = timeProvider.GetUtcNow();
        if (!validationResult.IsValid)
        {
            return new TransactionExecutionResult(
                false,
                new TransactionResponse(transactionId, MessageConstants.Status.Failed, processedAt),
                validationResult.Error,
                null,
                null);
        }

        fromAccount!.Balance -= amount;
        fromAccount.UpdatedAt = processedAt.UtcDateTime;
        toAccount!.Balance += amount;
        toAccount.UpdatedAt = processedAt.UtcDateTime;

        return new TransactionExecutionResult(
            true,
            new TransactionResponse(transactionId, MessageConstants.Status.Completed, processedAt),
            null,
            fromAccount.Balance,
            toAccount.Balance);
    }

    private async Task<(Account? FromAccount, Account? ToAccount)> LoadAccountsAsync(
        string fromAccountNumber,
        string toAccountNumber,
        CancellationToken cancellationToken)
    {
        if (lockRelease == LockRelease.AfterPublish)
        {
            await HoldUntilPublishedAsync(fromAccountNumber, toAccountNumber, cancellationToken);
        }

        if (lockStrategy == LockStrategy.Partition)
        {
            // One lane per partition, ids ascending: no deadlocks, no per-row ordering dance.
            foreach (var partitionId in PartitionIds(fromAccountNumber, toAccountNumber))
            {
                await accountRepository.LockPartitionAsync(partitionId, cancellationToken);
            }

            var fromAccount = await accountRepository.FindByAccountNumberAsync(fromAccountNumber, cancellationToken);
            var toAccount = string.Equals(fromAccountNumber, toAccountNumber, StringComparison.Ordinal)
                ? fromAccount
                : await accountRepository.FindByAccountNumberAsync(toAccountNumber, cancellationToken);
            return (fromAccount, toAccount);
        }

        if (string.Equals(fromAccountNumber, toAccountNumber, StringComparison.Ordinal))
        {
            var account = await accountRepository.LockForUpdateAsync(fromAccountNumber, cancellationToken);
            return (account, account);
        }

        var fromAccountLocksFirst = string.CompareOrdinal(fromAccountNumber, toAccountNumber) < 0;
        var firstAccountNumber = fromAccountLocksFirst ? fromAccountNumber : toAccountNumber;
        var secondAccountNumber = fromAccountLocksFirst ? toAccountNumber : fromAccountNumber;

        var firstAccount = await accountRepository.LockForUpdateAsync(firstAccountNumber, cancellationToken);
        var secondAccount = await accountRepository.LockForUpdateAsync(secondAccountNumber, cancellationToken);

        return fromAccountLocksFirst
            ? (firstAccount, secondAccount)
            : (secondAccount, firstAccount);
    }

    /// <summary>
    /// <c>Demo:LockRelease = afterPublish</c>, the planted flaw: session-scoped holds on the
    /// keys this strategy serializes on, taken before the strategy's own locks and in the
    /// same order (so a holder never waits on a lock another holder takes first), and
    /// given back by <see cref="TransactionExecutionHandler"/> only once the transfer's
    /// outcome events have been published.
    /// </summary>
    private async Task HoldUntilPublishedAsync(string fromAccountNumber, string toAccountNumber, CancellationToken cancellationToken)
    {
        if (lockStrategy == LockStrategy.Partition)
        {
            foreach (var partitionId in PartitionIds(fromAccountNumber, toAccountNumber))
            {
                await accountRepository.HoldPartitionAsync(partitionId, cancellationToken);
            }

            return;
        }

        foreach (var accountNumber in new[] { fromAccountNumber, toAccountNumber }.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            await accountRepository.HoldAccountAsync(accountNumber, cancellationToken);
        }
    }

    private static IEnumerable<int> PartitionIds(string fromAccountNumber, string toAccountNumber) =>
        new[] { fromAccountNumber, toAccountNumber }
            .Select(accountNumber => PartitionHelper.GetPartitionId(accountNumber, PartitionCount))
            .Distinct()
            .Order();
}

internal sealed record TransactionExecutionResult(
    bool Success,
    TransactionResponse Response,
    string? ErrorReason,
    decimal? NewFromBalance,
    decimal? NewToBalance);
