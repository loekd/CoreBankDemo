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

        return await dbContext.ProjectedAccounts
            .FromSqlInterpolated($"SELECT * FROM \"ProjectedAccounts\" WHERE \"AccountNumber\" = {accountNumber} FOR UPDATE")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
    }
}
