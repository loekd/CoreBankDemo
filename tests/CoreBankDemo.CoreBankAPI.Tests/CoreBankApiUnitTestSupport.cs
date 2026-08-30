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
