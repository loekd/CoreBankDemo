namespace CoreBankDemo.Persistence.IntegrationTests.Infrastructure;

/// <summary>
/// Deterministic <see cref="System.TimeProvider"/> for the persistence tier
/// (constraints §3: <c>TimeProvider</c> is injected, never <c>DateTime.UtcNow</c>),
/// so timestamps written to PostgreSQL are exact and assertable.
/// </summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
