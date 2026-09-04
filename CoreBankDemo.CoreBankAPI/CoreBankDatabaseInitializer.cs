using System.Data;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI;

internal static class CoreBankDatabaseInitializer
{
    private const long AdvisoryLockId = 0x434F524542414E4B;

    internal static async Task InitializeAsync(
        CoreBankDbContext dbContext,
        DemoAccountSeeder seeder,
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
                await seeder.SeedAsync(cancellationToken).ConfigureAwait(false);
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
