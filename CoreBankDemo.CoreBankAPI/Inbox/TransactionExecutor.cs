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
        if (lockStrategy == LockStrategy.Partition)
        {
            // One lane per partition, ids ascending: no deadlocks, no per-row ordering dance.
            var partitionIds = new[] { fromAccountNumber, toAccountNumber }
                .Select(accountNumber => PartitionHelper.GetPartitionId(accountNumber, PartitionCount))
                .Distinct()
                .Order();
            foreach (var partitionId in partitionIds)
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
}

internal sealed record TransactionExecutionResult(
    bool Success,
    TransactionResponse Response,
    string? ErrorReason,
    decimal? NewFromBalance,
    decimal? NewToBalance);
