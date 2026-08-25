using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI.Inbox;

internal interface IAccountRepository
{
    Task<Account?> LockForUpdateAsync(string accountNumber, CancellationToken cancellationToken);
    Task<Account?> FindByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken);
}

internal sealed class AccountRepository(CoreBankDbContext dbContext) : IAccountRepository
{
    [ExcludeFromCodeCoverage(Justification = "Postgres-only SELECT ... FOR UPDATE pass-through; covered by the k6/Postgres acceptance tier.")]
    public Task<Account?> LockForUpdateAsync(string accountNumber, CancellationToken cancellationToken) =>
        dbContext.Accounts
            .FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"AccountNumber\" = {accountNumber} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken);

    public Task<Account?> FindByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken) =>
        dbContext.Accounts.FirstOrDefaultAsync(
            account => account.AccountNumber == accountNumber,
            cancellationToken);
}
