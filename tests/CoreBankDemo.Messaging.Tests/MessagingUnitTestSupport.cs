namespace CoreBankDemo.Messaging.Tests;

/// <summary>
/// Docker-free unit-tier doubles (ADR-016 tier 1). These are plain message
/// shapes and a deterministic clock — no <see cref="Microsoft.EntityFrameworkCore.DbContext"/>,
/// no provider, no database. The equivalents that are actually persisted live
/// in <c>CoreBankDemo.Persistence.IntegrationTests</c>.
/// </summary>
public sealed class TestInboxMessage : IInboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int PartitionId { get; set; }
    public string Status { get; set; } = MessageConstants.Status.Pending;
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string? TraceParent { get; set; }
    public string? TraceState { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public int Priority { get; set; }
    public DateTime? HoldUntil { get; set; }
}

/// <inheritdoc cref="TestInboxMessage"/>
public sealed class TestOutboxEventMessage : IOutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int PartitionId { get; set; }
    public string Status { get; set; } = MessageConstants.Status.Pending;
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string? TraceParent { get; set; }
    public string? TraceState { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int Priority { get; set; }
    public DateTime? HoldUntil { get; set; }
}

/// <summary>Minimal deterministic <see cref="TimeProvider"/> for tests that need one.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
