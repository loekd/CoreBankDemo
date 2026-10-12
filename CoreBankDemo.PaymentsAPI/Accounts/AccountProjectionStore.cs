namespace CoreBankDemo.PaymentsAPI.Accounts;

internal sealed class AccountProjectionStore(PaymentsDbContext dbContext, TimeProvider timeProvider) : IAccountProjectionStore
{
    public async Task SettleAsync(string accountNumber, decimal newBalance, string currency, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var row = await ProjectedAccountRows.LockAsync(dbContext, accountNumber, now, cancellationToken).ConfigureAwait(false);
        row.SettledBalance = newBalance;
        row.Currency = currency;
        row.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<decimal> ReleaseAsync(string accountNumber, decimal amount, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var row = await ProjectedAccountRows.LockAsync(dbContext, accountNumber, now, cancellationToken).ConfigureAwait(false);
        var released = Math.Min(amount, row.Reserved);
        row.Reserved -= released;
        row.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return amount - released;
    }
}
