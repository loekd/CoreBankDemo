using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Infrastructure;

/// <summary>
/// Base class for every persistence test: one isolated PostgreSQL database per
/// test method, created inside the assembly-wide container fixture.
/// </summary>
/// <remarks>
/// xUnit constructs a new test-class instance per test method, so each test
/// gets an empty database of its own. That is what lets the whole assembly run
/// in parallel without globally disabling parallelism — which would hide, not
/// fix, shared-state bugs. Tests that deliberately need two competing writers
/// share only this one isolated database and open genuinely separate
/// connections/contexts through <see cref="CreateContext{TContext}"/>.
/// </remarks>
public abstract class PostgresDatabaseTestBase(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private string? _connectionString;

    protected PostgresContainerFixture Fixture { get; } = fixture;

    /// <summary>Connection string of this test's own database.</summary>
    protected string ConnectionString =>
        _connectionString ?? throw new InvalidOperationException("The isolated database has not been initialized yet.");

    protected FixedTimeProvider TimeProvider { get; } = new();

    public async ValueTask InitializeAsync()
    {
        _connectionString = await Fixture.CreateDatabaseAsync(GetType().Name, TestContext.Current.CancellationToken);
        await InitializeSchemaAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionString is not null)
        {
            await Fixture.DropDatabaseAsync(_connectionString);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Creates the schema for this test's database. Follows the application's
    /// own <c>EnsureCreatedAsync</c> strategy (constraints §3: never EF
    /// migrations) so the tested schema is the one production creates.
    /// </summary>
    protected abstract Task InitializeSchemaAsync(CancellationToken cancellationToken);

    /// <summary>
    /// A brand-new <typeparamref name="TContext"/> on its own Npgsql connection
    /// to this test's isolated database — the seam concurrency and locking
    /// tests need in order to be genuinely concurrent.
    /// </summary>
    protected TContext CreateContext<TContext>()
        where TContext : DbContext =>
        (TContext)Activator.CreateInstance(typeof(TContext), CreateOptions<TContext>())!;

    private protected DbContextOptions<TContext> CreateOptions<TContext>()
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(ConnectionString)
            .Options;
}
