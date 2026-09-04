using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI.Inbox;

internal interface IAccountRepository
{
    Task<Account?> LockForUpdateAsync(string accountNumber, CancellationToken cancellationToken);
    Task<Account?> FindByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken);
}

internal sealed class AccountRepository(CoreBankDbContext dbContext) : IAccountRepository
{
    /// <summary>
    /// Pessimistic row lock on the account (ADR-016): proved directly against
    /// real PostgreSQL with competing connections by
    /// <c>CoreBankDemo.Persistence.IntegrationTests</c>, never excluded from
    /// coverage and never re-routed through a provider-neutral load.
    /// </summary>
    public Task<Account?> LockForUpdateAsync(string accountNumber, CancellationToken cancellationToken) =>
        dbContext.Accounts
            .FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"AccountNumber\" = {accountNumber} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken);

    public Task<Account?> FindByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken) =>
        dbContext.Accounts.FirstOrDefaultAsync(
            account => account.AccountNumber == accountNumber,
            cancellationToken);
}
