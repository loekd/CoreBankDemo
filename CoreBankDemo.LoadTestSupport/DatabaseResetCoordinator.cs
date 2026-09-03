using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.LoadTestSupport;

internal interface ILoadTestDatabaseResetter
{
    Task<DatabaseResetResult> ResetAsync(CancellationToken cancellationToken);
}

internal sealed record DatabaseResetResult(int AccountsReset, decimal TotalBalance);

internal sealed class DatabaseResetState
{
    internal SemaphoreSlim Mutex { get; } = new(1, 1);
    internal DatabaseResetResult? Result { get; set; }
    internal Exception? ReleaseFailure { get; set; }
}

internal sealed class LoadTestDatabaseResetter(
    CoreBankDbContext coreBankDb,
    PaymentsDbContext paymentsDb) : ILoadTestDatabaseResetter
{
    public async Task<DatabaseResetResult> ResetAsync(CancellationToken cancellationToken)
    {
        await using var paymentsTransaction =
            await paymentsDb.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var coreBankTransaction =
            await coreBankDb.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await paymentsDb.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE \"OutboxMessages\" RESTART IDENTITY CASCADE", cancellationToken).ConfigureAwait(false);
        await paymentsDb.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE \"InboxMessages\" RESTART IDENTITY CASCADE", cancellationToken).ConfigureAwait(false);
        await coreBankDb.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE \"InboxMessages\" RESTART IDENTITY CASCADE", cancellationToken).ConfigureAwait(false);
        await coreBankDb.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE \"MessagingOutboxMessages\" RESTART IDENTITY CASCADE", cancellationToken).ConfigureAwait(false);

        var accountCount = await coreBankDb.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Accounts\" SET \"Balance\" = {LoadTestConstants.InitialBalance}, \"UpdatedAt\" = NULL WHERE \"AccountNumber\" LIKE '%LOAD%'",
            cancellationToken).ConfigureAwait(false);

        await paymentsTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await coreBankTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new DatabaseResetResult(
            accountCount,
            accountCount * LoadTestConstants.InitialBalance);
    }
}

internal sealed class DatabaseResetCoordinator(
    ILoadTestDatabaseResetter resetter,
    IProcessorStartGatePublisher startGatePublisher,
    DatabaseResetState state)
{
    internal async Task<DatabaseResetResult> ResetAndReleaseAsync(CancellationToken cancellationToken)
    {
        await state.Mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.ReleaseFailure is not null)
            {
                throw new InvalidOperationException(
                    "Processor release previously failed; restart the load-test AppHost before retrying reset.",
                    state.ReleaseFailure);
            }

            var alreadyReleased = state.Result is not null
                || await startGatePublisher.HasReleaseGenerationAsync(cancellationToken).ConfigureAwait(false);
            var result = await resetter.ResetAsync(cancellationToken).ConfigureAwait(false);
            if (alreadyReleased)
            {
                state.Result = result;
                return result;
            }

            try
            {
                await startGatePublisher.ReleaseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                state.ReleaseFailure = exception;
                throw;
            }

            state.Result = result;
            return result;
        }
        finally
        {
            state.Mutex.Release();
        }
    }
}
