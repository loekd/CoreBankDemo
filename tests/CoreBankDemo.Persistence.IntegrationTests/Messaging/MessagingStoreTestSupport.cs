using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.Persistence.IntegrationTests.Messaging;

/// <summary>
/// Command-shape test message: dedupes on <see cref="IdempotencyKey"/> alone
/// (AD-4 command store). The check constraint on <see cref="RetryCount"/>
/// (configured in <see cref="TestMessagingDbContext"/>) exists purely so tests
/// can produce a real, non-unique-violation <see cref="DbUpdateException"/> —
/// on PostgreSQL that surfaces as SQLSTATE <c>23514</c>, which must never be
/// classified as a duplicate.
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

public class TestMessagingDbContext(DbContextOptions<TestMessagingDbContext> options) : DbContext(options)
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
            // DbUpdateException (PostgreSQL SQLSTATE 23514 — check_violation);
            // such failures must propagate unchanged.
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
/// Per-test-method PostgreSQL database inside the shared container fixture
/// (ADR-016 tier 2). Every <see cref="CreateContext()"/> call returns a context
/// on its own Npgsql connection, which is exactly what the concurrency and
/// locking assertions in this folder require.
/// </summary>
public abstract class MessagingPostgresTestBase(PostgresContainerFixture fixture)
    : PostgresDatabaseTestBase(fixture)
{
    protected override async Task InitializeSchemaAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    /// <summary>A fresh <see cref="TestMessagingDbContext"/> on its own connection to this test's database.</summary>
    protected TestMessagingDbContext CreateContext() => CreateContext<TestMessagingDbContext>();
}
