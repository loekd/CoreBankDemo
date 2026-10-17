using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI.Inbox;

internal interface IAccountRepository
{
    Task<Account?> LockForUpdateAsync(string accountNumber, CancellationToken cancellationToken);
    Task<Account?> FindByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken);
    Task LockPartitionAsync(int partitionId, CancellationToken cancellationToken);
    Task HoldPartitionAsync(int partitionId, CancellationToken cancellationToken);
    Task HoldAccountAsync(string accountNumber, CancellationToken cancellationToken);
    Task ReleaseHeldLocksAsync(CancellationToken cancellationToken);
}

internal sealed class AccountRepository(CoreBankDbContext dbContext) : IAccountRepository
{
    /// <summary>
    /// Pessimistic row lock on the account (ADR-016): proved directly against
    /// real PostgreSQL with competing connections by
    /// <c>CoreBankDemo.Persistence.IntegrationTests</c>, never excluded from
    /// coverage and never re-routed through a provider-neutral load.
    /// <c>SingleOrDefault</c>, not <c>FirstOrDefault</c>: the account number is
    /// the primary key, and EF Core cannot see the raw SQL's filter, so
    /// <c>First</c> would log a missing-OrderBy warning on every locked read.
    /// </summary>
    public Task<Account?> LockForUpdateAsync(string accountNumber, CancellationToken cancellationToken) =>
        dbContext.Accounts
            .FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"AccountNumber\" = {accountNumber} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<Account?> FindByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken) =>
        dbContext.Accounts.FirstOrDefaultAsync(
            account => account.AccountNumber == accountNumber,
            cancellationToken);

    /// <summary>
    /// Transaction-scoped advisory lock on a partition id: held until the
    /// ambient transaction commits or rolls back, so it must run inside
    /// <c>ExecuteInTransactionAsync</c>.
    /// </summary>
    public Task LockPartitionAsync(int partitionId, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({(long)partitionId})",
            cancellationToken);

    /// <summary>
    /// Session-scoped advisory lock on a partition id (<c>Demo:LockRelease = afterPublish</c>,
    /// key space 1): unlike the transaction-scoped lock it survives the commit, so
    /// <see cref="TransactionExecutionHandler"/> can keep the partition until the transfer's
    /// outcome events have left the messaging outbox. It lives on the connection, so the
    /// caller pins that connection open (<c>Database.OpenConnectionAsync</c>) and releases
    /// through <see cref="ReleaseHeldLocksAsync"/> before closing it.
    /// </summary>
    public Task HoldPartitionAsync(int partitionId, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_lock(1, {partitionId})",
            cancellationToken);

    /// <summary>
    /// Session-scoped advisory lock on an account number (<c>Demo:LockRelease = afterPublish</c>,
    /// key space 2, keyed by PostgreSQL's <c>hashtext</c> of the number); same lifetime and
    /// release rules as <see cref="HoldPartitionAsync"/>.
    /// </summary>
    public Task HoldAccountAsync(string accountNumber, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_lock(2, hashtext({accountNumber}))",
            cancellationToken);

    /// <summary>
    /// Releases every session advisory lock this connection holds, however many times
    /// each was taken (a transient-failure retry of the transaction takes them again).
    /// </summary>
    public Task ReleaseHeldLocksAsync(CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock_all()", cancellationToken);
}
