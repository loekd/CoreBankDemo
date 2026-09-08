using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI.Tests;

/// <summary>
/// Minimal deterministic <see cref="TimeProvider"/> for the Docker-free unit
/// tier (ADR-016 tier 1).
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public static class CoreBankApiUnitTestSupport
{
    /// <summary>
    /// A <see cref="CoreBankDbContext"/> on the real Npgsql provider that never
    /// opens a connection: only its change tracker is exercised (attach,
    /// detach, entry state), which needs the model but no database. Not a
    /// PostgreSQL substitute (constraints §4) -- every query and save is
    /// proved in the Testcontainers tier; this exists so the unit tier can
    /// assert what a handler leaves tracked without Docker.
    /// </summary>
    public static CoreBankDbContext DetachedDbContext() =>
        new(new DbContextOptionsBuilder<CoreBankDbContext>()
            .UseNpgsql("Host=localhost;Port=1;Database=unit-tier-never-connects;Username=x;Password=x")
            .Options);
}
