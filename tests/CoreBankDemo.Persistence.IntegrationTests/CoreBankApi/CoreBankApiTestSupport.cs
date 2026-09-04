using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.Persistence.IntegrationTests.CoreBankApi;

/// <summary>
/// Per-test-method PostgreSQL database for the CoreBankAPI stores (ADR-016
/// tier 2), created inside the assembly-wide container fixture. The schema is
/// produced by the application's own <c>EnsureCreatedAsync</c> path, so these
/// tests observe exactly the tables, indexes, and column types production
/// creates.
/// </summary>
public abstract class CoreBankApiPostgresTestBase(PostgresContainerFixture fixture)
    : PostgresDatabaseTestBase(fixture)
{
    protected override async Task InitializeSchemaAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    /// <summary>A fresh <see cref="CoreBankDbContext"/> on its own connection to this test's database.</summary>
    protected CoreBankDbContext CreateContext() => CreateContext<CoreBankDbContext>();
}
