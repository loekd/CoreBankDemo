using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Messaging;

/// <summary>
/// <c>MarkAsCompletedAsync</c> on <see cref="MessageRepositoryBase{TMessage,TDbContext}"/>
/// (story 2.4): the success path <see cref="OutboxProcessorBase{TMessage}"/>
/// calls after a delivery strategy returns without throwing. Mirrors
/// <see cref="MarkAsFailedWithRetryAsync"/>'s detached-attach and
/// concurrency-conflict-retry handling (same concurrency-token-on-Status
/// design, see <see cref="MarkAsFailedWithRetryAsyncTests"/>) because this
/// method mutates the same concurrency-tokened <c>Status</c> column.
/// </summary>
public class MarkAsCompletedAsyncTests(PostgresContainerFixture fixture) : MessagingPostgresTestBase(fixture)
{
    [Fact]
    public async Task Completes_pending_message_and_stamps_processed_at_from_time_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider);

        var message = new TestOutboxEventMessage { IdempotencyKey = "complete-me", EventType = "Debited" };
        context.OutboxEventMessages.Add(message);
        await context.SaveChangesAsync(ct);

        TimeProvider.Advance(TimeSpan.FromSeconds(5));
        var expectedProcessedAt = TimeProvider.GetUtcNow().UtcDateTime;

        await repository.MarkAsCompletedAsync(message, ct);

        message.Status.Should().Be(MessageConstants.Status.Completed);
        message.ProcessedAt.Should().Be(expectedProcessedAt);

        var reloaded = await context.OutboxEventMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, ct);
        reloaded.Status.Should().Be(MessageConstants.Status.Completed);
        reloaded.ProcessedAt.Should().Be(expectedProcessedAt);
    }

    [Fact]
    public async Task Detached_message_from_a_different_context_is_attached_and_the_completion_persists()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seedContext = CreateContext();
        var seeded = new TestOutboxEventMessage { IdempotencyKey = "detached-complete", EventType = "Debited" };
        seedContext.OutboxEventMessages.Add(seeded);
        await seedContext.SaveChangesAsync(ct);

        await using var loadContext = CreateContext();
        var detachedCopy = await loadContext.OutboxEventMessages.AsNoTracking().SingleAsync(m => m.Id == seeded.Id, ct);

        await using var repoContext = CreateContext();
        var repository = new TestOutboxEventMessageRepository(repoContext, TimeProvider);
        repoContext.Entry(detachedCopy).State.Should().Be(EntityState.Detached);

        await repository.MarkAsCompletedAsync(detachedCopy, ct);

        var reloaded = await repoContext.OutboxEventMessages.AsNoTracking().SingleAsync(m => m.Id == seeded.Id, ct);
        reloaded.Status.Should().Be(MessageConstants.Status.Completed);
        reloaded.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Concurrency_conflict_from_a_concurrent_claim_retries_once_and_persists()
    {
        // Same rationale as MarkAsFailedWithRetryAsyncTests' equivalent test:
        // Status is a concurrency token, so a stale in-memory copy racing a
        // concurrent claim must be retried once against reloaded values rather
        // than letting DbUpdateConcurrencyException escape undocumented.
        var ct = TestContext.Current.CancellationToken;
        await using var seedContext = CreateContext();
        var seeded = new TestOutboxEventMessage { IdempotencyKey = "concurrency-complete", EventType = "Debited" };
        seedContext.OutboxEventMessages.Add(seeded);
        await seedContext.SaveChangesAsync(ct);

        await using var completeContext = CreateContext();
        var messageForCompletion = await completeContext.OutboxEventMessages.SingleAsync(m => m.Id == seeded.Id, ct);
        var completeRepository = new TestOutboxEventMessageRepository(completeContext, TimeProvider);

        await using var claimContext = CreateContext();
        var claimRepository = new TestOutboxEventMessageRepository(claimContext, TimeProvider);
        var claimed = await claimRepository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);
        claimed.Should().ContainSingle(m => m.Id == seeded.Id);

        var act = async () => await completeRepository.MarkAsCompletedAsync(messageForCompletion, ct);

        await act.Should().NotThrowAsync();

        var reloaded = await seedContext.OutboxEventMessages.AsNoTracking().SingleAsync(m => m.Id == seeded.Id, ct);
        reloaded.Status.Should().Be(MessageConstants.Status.Completed);
        reloaded.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Repeat_call_on_an_already_completed_message_is_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider);

        var originalProcessedAt = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var message = new TestOutboxEventMessage
        {
            IdempotencyKey = "already-completed",
            EventType = "Debited",
            Status = MessageConstants.Status.Completed,
            ProcessedAt = originalProcessedAt,
        };
        context.OutboxEventMessages.Add(message);
        await context.SaveChangesAsync(ct);

        TimeProvider.Advance(TimeSpan.FromSeconds(5));

        await repository.MarkAsCompletedAsync(message, ct);

        message.Status.Should().Be(MessageConstants.Status.Completed);
        message.ProcessedAt.Should().Be(originalProcessedAt, "a no-op must not re-stamp ProcessedAt");

        var reloaded = await context.OutboxEventMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, ct);
        reloaded.ProcessedAt.Should().Be(originalProcessedAt);
    }

    [Fact]
    public async Task Call_on_an_already_failed_message_is_a_no_op_and_does_not_revive_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider);

        var message = new TestOutboxEventMessage
        {
            IdempotencyKey = "already-failed",
            EventType = "Debited",
            Status = MessageConstants.Status.Failed,
            RetryCount = MessageConstants.Defaults.MaxRetryCount,
            LastError = "gave up",
        };
        context.OutboxEventMessages.Add(message);
        await context.SaveChangesAsync(ct);

        await repository.MarkAsCompletedAsync(message, ct);

        message.Status.Should().Be(MessageConstants.Status.Failed,
            "a late-arriving completion report must not revive a row already given up on");
        message.ProcessedAt.Should().BeNull();

        var reloaded = await context.OutboxEventMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, ct);
        reloaded.Status.Should().Be(MessageConstants.Status.Failed);
        reloaded.ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task Rejects_null_message()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider);

        var act = async () => await repository.MarkAsCompletedAsync(null!, ct);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("message");
    }
}
