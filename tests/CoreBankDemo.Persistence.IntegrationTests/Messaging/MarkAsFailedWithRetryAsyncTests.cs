using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Messaging;

/// <summary>
/// <c>MarkAsFailedWithRetryAsync</c> on <see cref="InboxMessageRepositoryBase{TMessage,TDbContext}"/>
/// / <see cref="OutboxMessageRepositoryBase{TMessage,TDbContext}"/> (story 2.3):
/// retry-under-limit vs. terminal-poison-at-limit, per AD-11 (transport-only —
/// this method never encodes business rejection).
/// </summary>
public class MarkAsFailedWithRetryAsyncTests(PostgresContainerFixture fixture) : MessagingPostgresTestBase(fixture)
{
    [Fact]
    public async Task Retry_under_limit_returns_to_pending_and_increments_retry_count()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var message = new TestInboxMessage { IdempotencyKey = "under-limit", RetryCount = 2 };
        context.InboxMessages.Add(message);
        await context.SaveChangesAsync(ct);

        await repository.MarkAsFailedWithRetryAsync(message, "transient transport error", ct);

        message.Status.Should().Be(MessageConstants.Status.Pending);
        message.RetryCount.Should().Be(3);
        message.LastError.Should().Be("transient transport error");

        var reloaded = await context.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, ct);
        reloaded.Status.Should().Be(MessageConstants.Status.Pending);
        reloaded.RetryCount.Should().Be(3);
        reloaded.LastError.Should().Be("transient transport error");
    }

    [Fact]
    public async Task Retry_at_limit_becomes_terminal_failed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var message = new TestInboxMessage
        {
            IdempotencyKey = "at-limit",
            RetryCount = MessageConstants.Defaults.MaxRetryCount - 1,
        };
        context.InboxMessages.Add(message);
        await context.SaveChangesAsync(ct);

        await repository.MarkAsFailedWithRetryAsync(message, "still failing", ct);

        message.Status.Should().Be(MessageConstants.Status.Failed);
        message.RetryCount.Should().Be(MessageConstants.Defaults.MaxRetryCount);
        message.LastError.Should().Be("still failing");

        var reloaded = await context.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, ct);
        reloaded.Status.Should().Be(MessageConstants.Status.Failed);
    }

    [Fact]
    public async Task Repeat_call_on_an_already_failed_message_is_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var message = new TestInboxMessage
        {
            IdempotencyKey = "already-failed",
            Status = MessageConstants.Status.Failed,
            RetryCount = MessageConstants.Defaults.MaxRetryCount,
            LastError = "original failure",
        };
        context.InboxMessages.Add(message);
        await context.SaveChangesAsync(ct);

        await repository.MarkAsFailedWithRetryAsync(message, "another transport error", ct);

        message.Status.Should().Be(MessageConstants.Status.Failed);
        message.RetryCount.Should().Be(MessageConstants.Defaults.MaxRetryCount,
            "a repeat call on a terminal Failed row must not increment RetryCount further");
        message.LastError.Should().Be("original failure", "a no-op must not overwrite LastError either");

        var reloaded = await context.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, ct);
        reloaded.RetryCount.Should().Be(MessageConstants.Defaults.MaxRetryCount);
        reloaded.LastError.Should().Be("original failure");
    }

    [Fact]
    public async Task Detached_message_from_a_different_context_is_attached_and_the_transition_persists()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seedContext = CreateContext();
        var seeded = new TestInboxMessage { IdempotencyKey = "detached-persist", RetryCount = 1 };
        seedContext.InboxMessages.Add(seeded);
        await seedContext.SaveChangesAsync(ct);

        // Loaded AsNoTracking on a separate context instance - this entity has
        // never been seen by repoContext below, so it starts life detached
        // relative to the repository's own DbContext.
        await using var loadContext = CreateContext();
        var detachedCopy = await loadContext.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == seeded.Id, ct);

        await using var repoContext = CreateContext();
        var repository = new TestInboxMessageRepository(repoContext, TimeProvider, TestBusinessMetrics.Instance);
        repoContext.Entry(detachedCopy).State.Should().Be(EntityState.Detached);

        await repository.MarkAsFailedWithRetryAsync(detachedCopy, "detached transport error", ct);

        var reloaded = await repoContext.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == seeded.Id, ct);
        reloaded.RetryCount.Should().Be(2, "the mutation must have actually been persisted, not silently dropped");
        reloaded.Status.Should().Be(MessageConstants.Status.Pending);
        reloaded.LastError.Should().Be("detached transport error");
    }

    [Fact]
    public async Task Concurrency_conflict_from_a_concurrent_claim_retries_once_and_persists()
    {
        // Status is a concurrency token (ConfigureConcurrencyToken). Simulates
        // a caller holding a stale in-memory copy of a row that a concurrent
        // claim flips from Pending to Processing underneath it - the first
        // SaveChangesAsync attempt must hit DbUpdateConcurrencyException, and
        // the method must retry once against the reloaded database values
        // rather than letting the exception escape undocumented.
        var ct = TestContext.Current.CancellationToken;
        await using var seedContext = CreateContext();
        var seeded = new TestInboxMessage { IdempotencyKey = "concurrency-retry-claim", RetryCount = 1 };
        seedContext.InboxMessages.Add(seeded);
        await seedContext.SaveChangesAsync(ct);

        await using var failContext = CreateContext();
        var messageForFailure = await failContext.InboxMessages.SingleAsync(m => m.Id == seeded.Id, ct);
        var failRepository = new TestInboxMessageRepository(failContext, TimeProvider, TestBusinessMetrics.Instance);

        // Concurrently, a different caller claims the same row (Pending ->
        // Processing), changing the Status concurrency token out from under
        // failRepository's already-loaded, now-stale in-memory copy.
        await using var claimContext = CreateContext();
        var claimRepository = new TestInboxMessageRepository(claimContext, TimeProvider, TestBusinessMetrics.Instance);
        var claimed = await claimRepository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);
        claimed.Should().ContainSingle(m => m.Id == seeded.Id);

        var act = async () => await failRepository.MarkAsFailedWithRetryAsync(messageForFailure, "transient", ct);

        await act.Should().NotThrowAsync();

        var reloaded = await seedContext.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == seeded.Id, ct);
        reloaded.RetryCount.Should().Be(2, "the retried transition should have applied against the reloaded row");
        reloaded.Status.Should().Be(MessageConstants.Status.Pending);
        reloaded.LastError.Should().Be("transient");
    }

    [Fact]
    public async Task Rejects_null_message()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var act = async () => await repository.MarkAsFailedWithRetryAsync(null!, "error", ct);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("message");
    }

    [Fact]
    public async Task Rejects_null_error_message()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var message = new TestInboxMessage { IdempotencyKey = "null-error" };
        context.InboxMessages.Add(message);
        await context.SaveChangesAsync(ct);

        var act = async () => await repository.MarkAsFailedWithRetryAsync(message, null!, ct);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("errorMessage");
    }
}
