using System.Data;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.PaymentsAPI;

internal static class PaymentsDatabaseInitializer
{
    private const long AdvisoryLockId = 0x5041594D454E5453;

    internal static async Task InitializeAsync(
        PaymentsDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        var connection = dbContext.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_lock({AdvisoryLockId})", cancellationToken).ConfigureAwait(false);
            try
            {
                await dbContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_unlock({AdvisoryLockId})", CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (closeConnection)
            {
                await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }
}
