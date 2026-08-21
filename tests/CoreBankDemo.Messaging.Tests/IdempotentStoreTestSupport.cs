using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Messaging.Tests;

/// <summary>
/// Command-shape test message: dedupes on <see cref="IdempotencyKey"/> alone
/// (AD-4 command store). One row per distinct <see cref="RetryCount"/>-checked
/// insert; the check constraint on <see cref="RetryCount"/> (configured in
/// <see cref="TestMessagingDbContext"/>) exists purely so tests can produce a
/// real, non-unique-violation <see cref="DbUpdateException"/> on SQLite.
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
}

/// <summary>
/// Event-shape test message: dedupes on the composite
/// (<see cref="IOutboxMessage.IdempotencyKey"/>, <see cref="EventType"/>) pair
/// (AD-4 event store) — the same key may legitimately appear more than once as
/// long as the event type differs, mirroring one transaction yielding several
/// distinct events.
/// </summary>
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
}

public sealed class TestMessagingDbContext(DbContextOptions<TestMessagingDbContext> options) : DbContext(options)
{
    public DbSet<TestInboxMessage> InboxMessages => Set<TestInboxMessage>();

    public DbSet<TestOutboxEventMessage> OutboxEventMessages => Set<TestOutboxEventMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TestInboxMessage>(builder =>
        {
            builder.HasKey(m => m.Id);
            InboxMessageRepositoryBase<TestInboxMessage, TestMessagingDbContext>.ConfigureDedupeIndex(
                builder, nameof(TestInboxMessage.IdempotencyKey));
            InboxMessageRepositoryBase<TestInboxMessage, TestMessagingDbContext>.ConfigureConcurrencyToken(builder);

            // Exists only so tests can trigger a real, non-unique-violation
            // DbUpdateException on SQLite (spec: such failures must propagate).
            builder.ToTable(t => t.HasCheckConstraint(
                "CK_TestInboxMessage_RetryCount", $"\"{nameof(TestInboxMessage.RetryCount)}\" >= 0"));
        });

        modelBuilder.Entity<TestOutboxEventMessage>(builder =>
        {
            builder.HasKey(m => m.Id);
            OutboxMessageRepositoryBase<TestOutboxEventMessage, TestMessagingDbContext>.ConfigureDedupeIndex(
                builder, nameof(TestOutboxEventMessage.IdempotencyKey), nameof(TestOutboxEventMessage.EventType));
            OutboxMessageRepositoryBase<TestOutboxEventMessage, TestMessagingDbContext>.ConfigureConcurrencyToken(builder);
        });
    }
}

public sealed class TestInboxMessageRepository(TestMessagingDbContext dbContext, TimeProvider timeProvider)
    : InboxMessageRepositoryBase<TestInboxMessage, TestMessagingDbContext>(dbContext, timeProvider)
{
    protected override DbSet<TestInboxMessage> InboxMessages => DbContext.InboxMessages;
}

public sealed class TestOutboxEventMessageRepository(TestMessagingDbContext dbContext, TimeProvider timeProvider)
    : OutboxMessageRepositoryBase<TestOutboxEventMessage, TestMessagingDbContext>(dbContext, timeProvider)
{
    protected override DbSet<TestOutboxEventMessage> OutboxMessages => DbContext.OutboxEventMessages;
}

/// <summary>
/// Per-test-method SQLite in-memory database (AD-9 store tier). Implementing
/// <see cref="IAsyncLifetime"/> directly on a test class gives each test method
/// its own fresh, empty database (xUnit constructs a new test-class instance
/// per test) — no cross-test row bleed, and no shared-fixture bookkeeping.
/// Uses a named shared-cache database (rather than a single kept-open
/// connection) so multiple independent <see cref="SqliteConnection"/>s — as a
/// genuine concurrency test needs — see the same in-memory data.
/// </summary>
public abstract class SqliteMessagingTestBase : IAsyncLifetime
{
    private readonly string _databaseName = $"messaging-tests-{Guid.NewGuid():N}";
    private SqliteConnection? _keepAliveConnection;

    protected FakeTimeProvider TimeProvider { get; } = new();

    private string ConnectionString => $"Data Source=file:{_databaseName};Mode=Memory;Cache=Shared";

    public async ValueTask InitializeAsync()
    {
        // Keeps the shared-cache in-memory database alive for the lifetime of
        // the test; without an open connection, SQLite drops it as soon as the
        // last connection to it closes.
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

    /// <summary>A fresh <see cref="TestMessagingDbContext"/> backed by its own connection to the shared in-memory database.</summary>
    protected TestMessagingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestMessagingDbContext>()
            .UseSqlite(ConnectionString)
            .Options;
        return new TestMessagingDbContext(options);
    }
}

/// <summary>Minimal deterministic <see cref="TimeProvider"/> for tests that need one to satisfy repository constructors.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
