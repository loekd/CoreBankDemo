using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.PaymentsAPI.Accounts;

/// <summary>
/// The one place that knows how a <see cref="ProjectedAccount"/> row is
/// created and locked (ruling A6: raw SQL stays in persistence classes). Both
/// writers -- the accept path in <c>OutboxRepository.AcceptAsync</c> and the
/// inbox path in <see cref="AccountProjectionStore"/> -- lock through here, so
/// a debit and a settlement for one account serialise on the same row lock.
/// </summary>
internal static class ProjectedAccountRows
{
    /// <summary>
    /// Creates the row if it does not exist (<c>ON CONFLICT DO NOTHING</c>, so
    /// two first-time writers cannot both fail to create it) and then takes a
    /// <c>SELECT … FOR UPDATE</c> lock on it. Must run inside an open
    /// transaction: the lock is released when that transaction ends.
    /// <para>
    /// The returned instance always carries the database's values. An
    /// execution-strategy retry re-runs the caller's transaction on the same
    /// context, and an instance the rolled-back attempt left tracked would
    /// otherwise come back from the tracked query unchanged (EF identity
    /// resolution) -- releasing from a stale <c>Reserved</c> loses or doubles
    /// the release. So any tracked row for this account is detached first and
    /// the query materialises a fresh one.
    /// </para>
    /// </summary>
    public static async Task<ProjectedAccount> LockAsync(
        PaymentsDbContext dbContext,
        string accountNumber,
        DateTime now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrEmpty(accountNumber);

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "ProjectedAccounts" ("AccountNumber", "SettledBalance", "Reserved", "Currency", "UpdatedAt")
             VALUES ({accountNumber}, NULL, 0, NULL, {now})
             ON CONFLICT ("AccountNumber") DO NOTHING
             """,
            cancellationToken).ConfigureAwait(false);

        var stale = dbContext.ChangeTracker.Entries<ProjectedAccount>()
            .FirstOrDefault(entry => entry.Entity.AccountNumber == accountNumber);
        if (stale is not null)
        {
            stale.State = EntityState.Detached;
        }

        return await dbContext.ProjectedAccounts
            .FromSqlInterpolated($"SELECT * FROM \"ProjectedAccounts\" WHERE \"AccountNumber\" = {accountNumber} FOR UPDATE")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
    }
}
