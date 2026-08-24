using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.CoreBankAPI.Tests;

/// <summary>
/// Per-test-method SQLite in-memory database (AD-9 store tier), mirroring
/// <c>CoreBankDemo.Messaging.Tests</c>'s <c>SqliteMessagingTestBase</c>.
/// Implementing <see cref="IAsyncLifetime"/> directly on a test class gives
/// each test method its own fresh, empty database (xUnit constructs a new
/// test-class instance per test) — no cross-test row bleed. Uses a named
/// shared-cache database so a single open keep-alive connection is enough to
/// keep SQLite from dropping the in-memory database between accesses.
/// </summary>
public abstract class SqliteCoreBankApiTestBase : IAsyncLifetime
{
    private readonly string _databaseName = $"corebankapi-tests-{Guid.NewGuid():N}";
    private SqliteConnection? _keepAliveConnection;

    protected FakeTimeProvider TimeProvider { get; } = new();

    private string ConnectionString => $"Data Source=file:{_databaseName};Mode=Memory;Cache=Shared";

    public async ValueTask InitializeAsync()
    {
        _keepAliveConnection = new SqliteConnection(ConnectionString);
        await _keepAliveConnection.OpenAsync();

        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    public ValueTask DisposeAsync()
    {
        _keepAliveConnection?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A fresh <see cref="CoreBankDbContext"/> backed by its own connection to the shared in-memory database.</summary>
    protected CoreBankDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoreBankDbContext>()
            .UseSqlite(ConnectionString)
            .Options;
        return new CoreBankDbContext(options);
    }
}

/// <summary>Minimal deterministic <see cref="TimeProvider"/> for tests that need one to satisfy constructors.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
