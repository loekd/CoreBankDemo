namespace CoreBankDemo.DemoRunner.Tests;

/// <summary>Minimal deterministic <see cref="TimeProvider"/>, mirroring the pattern used by the other test projects.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
