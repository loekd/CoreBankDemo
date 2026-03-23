using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI.Inbox;

public interface ITransactionExecutor
{
    Task<(Account? FromAccount, Account? ToAccount)> LoadAccountsAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        CancellationToken cancellationToken);

    (decimal NewFromBalance, decimal NewToBalance) ApplySuccessfulTransaction(
        InboxMessage message,
        Account fromAccount,
        Account toAccount);

    Task SaveAsync(CoreBankDbContext dbContext, CancellationToken cancellationToken);

    void PrepareFailedTransaction(InboxMessage message, string? error);
}

public class TransactionExecutor(TimeProvider timeProvider) : ITransactionExecutor
{
    public async Task<(Account? FromAccount, Account? ToAccount)> LoadAccountsAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        CancellationToken cancellationToken)
    {
        // Lock accounts in consistent alphabetical order to prevent deadlocks when
        // concurrent transactions involve the same accounts in opposite directions.
        var (firstKey, secondKey) = string.Compare(message.FromAccount, message.ToAccount, StringComparison.Ordinal) < 0
            ? (message.FromAccount, message.ToAccount)
            : (message.ToAccount, message.FromAccount);

        // SELECT FOR UPDATE acquires row-level locks for the duration of the transaction,
        // preventing lost updates when multiple partitions process messages concurrently.
        await dbContext.Accounts
            .FromSqlRaw("SELECT * FROM \"Accounts\" WHERE \"AccountNumber\" = {0} FOR UPDATE", firstKey)
            .FirstOrDefaultAsync(cancellationToken);

        await dbContext.Accounts
            .FromSqlRaw("SELECT * FROM \"Accounts\" WHERE \"AccountNumber\" = {0} FOR UPDATE", secondKey)
            .FirstOrDefaultAsync(cancellationToken);

        var fromAccount = dbContext.Accounts.Local.FirstOrDefault(a => a.AccountNumber == message.FromAccount);
        var toAccount = dbContext.Accounts.Local.FirstOrDefault(a => a.AccountNumber == message.ToAccount);

        return (fromAccount, toAccount);
    }

    public (decimal NewFromBalance, decimal NewToBalance) ApplySuccessfulTransaction(
        InboxMessage message,
        Account fromAccount,
        Account toAccount)
    {
        var processedAt = timeProvider.GetUtcNow();
        var transactionId = EnsureTransactionId(message);

        UpdateAccountBalances(fromAccount, toAccount, message.Amount, processedAt);
        UpdateMessageForSuccess(message, transactionId, processedAt);

        return (fromAccount.Balance, toAccount.Balance);
    }

    public Task SaveAsync(CoreBankDbContext dbContext, CancellationToken cancellationToken)
        => dbContext.SaveChangesAsync(cancellationToken);

    public void PrepareFailedTransaction(InboxMessage message, string? error)
    {
        var failedAt = timeProvider.GetUtcNow();
        var transactionId = EnsureTransactionId(message);

        UpdateMessageForFailure(message, transactionId, failedAt, error);
    }

    private static void UpdateAccountBalances(Account fromAccount, Account toAccount, decimal amount, DateTimeOffset processedAt)
    {
        fromAccount.Balance -= amount;
        fromAccount.UpdatedAt = processedAt.UtcDateTime;

        toAccount.Balance += amount;
        toAccount.UpdatedAt = processedAt.UtcDateTime;
    }

    private static void UpdateMessageForSuccess(InboxMessage message, string transactionId, DateTimeOffset processedAt)
    {
        message.TransactionId = transactionId;
        message.Status = "Completed";
        message.ProcessedAt = processedAt.UtcDateTime;
        message.ResponsePayload = JsonSerializer.Serialize(
            new TransactionResponse(transactionId, "Completed", processedAt));
    }

    private static void UpdateMessageForFailure(InboxMessage message, string transactionId, DateTimeOffset failedAt, string? error)
    {
        message.TransactionId = transactionId;
        message.Status = "Failed";
        message.LastError = error;
        message.ProcessedAt = failedAt.UtcDateTime;
    }

    private static string EnsureTransactionId(InboxMessage message)
    {
        return message.TransactionId ?? Guid.NewGuid().ToString();
    }
}
