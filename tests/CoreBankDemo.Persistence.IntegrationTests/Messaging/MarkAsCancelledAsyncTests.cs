using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Messaging;

/// <summary>
/// <c>MarkAsCancelledAsync</c> on <see cref="InboxMessageRepositoryBase{TMessage,TDbContext}"/>
/// / <see cref="OutboxMessageRepositoryBase{TMessage,TDbContext}"/> (spec:
/// instant-rail-timeout-cancel): the only path that writes terminal
/// <c>Cancelled</c>. Proves the transition stamps <c>ProcessedAt</c> and the
/// reason, is a no-op on any terminal row, never revives a row, and yields a
/// row that neither claim path will ever pick up again.
/// </summary>
public class MarkAsCancelledAsyncTests(PostgresContainerFixture fixture) : MessagingPostgresTestBase(fixture)
{
    [Fact]
    public async Task Cancels_a_claimed_row_and_stamps_processed_at_and_the_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var message = new TestOutboxEventMessage
        {
            IdempotencyKey = "cancel-me", EventType = "Debited", CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        context.OutboxEventMessages.Add(message);
        await context.SaveChangesAsync(ct);
        var claimed = await repository.TryClaimByIdAsync(message.Id, ct);
        TimeProvider.Advance(TimeSpan.FromSeconds(3));
        var expectedProcessedAt = TimeProvider.GetUtcNow().UtcDateTime;

        var outcome = await repository.MarkAsCancelledAsync(claimed!, "Instant rail budget exhausted", ct);

        outcome.Should().Be(MessageTransitionOutcome.Applied);
        var reloaded = await context.OutboxEventMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, ct);
        reloaded.Status.Should().Be(MessageConstants.Status.Cancelled);
        reloaded.ProcessedAt.Should().Be(expectedProcessedAt);
        reloaded.LastError.Should().Be("Instant rail budget exhausted");
        reloaded.RetryCount.Should().Be(0, "a cancel is not a transport failure and burns no retry");
    }

    [Fact]
    public async Task Cancels_a_pending_row_that_was_never_claimed()
    {
        // The tombstone/local-cancel shape: a row the caller stored moments
        // ago and cancels without ever forwarding it.
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var message = new TestInboxMessage { IdempotencyKey = "never-claimed", ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime };
        context.InboxMessages.Add(message);
        await context.SaveChangesAsync(ct);

        var outcome = await repository.MarkAsCancelledAsync(message, "cancelled before delivery", ct);

        outcome.Should().Be(MessageTransitionOutcome.Applied);
        var reloaded = await context.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, ct);
        reloaded.Status.Should().Be(MessageConstants.Status.Cancelled);
        reloaded.ProcessedAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData(MessageConstants.Status.Completed)]
    [InlineData(MessageConstants.Status.Failed)]
    [InlineData(MessageConstants.Status.Cancelled)]
    public async Task Call_on_a_terminal_row_is_a_no_op(string status)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var originalProcessedAt = new DateTime(2026, 9, 8, 11, 0, 0, DateTimeKind.Utc);
        var message = new TestOutboxEventMessage
        {
            IdempotencyKey = "terminal",
            EventType = "Debited",
            Status = status,
            ProcessedAt = originalProcessedAt,
            LastError = "original",
            CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        context.OutboxEventMessages.Add(message);
        await context.SaveChangesAsync(ct);
        TimeProvider.Advance(TimeSpan.FromSeconds(5));

        var outcome = await repository.MarkAsCancelledAsync(message, "late cancel", ct);

        outcome.Should().Be(MessageTransitionOutcome.AlreadyTerminal);
        var reloaded = await context.OutboxEventMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, ct);
        reloaded.Status.Should().Be(status, "a terminal row is never overwritten by a cancel");
        reloaded.ProcessedAt.Should().Be(originalProcessedAt);
        reloaded.LastError.Should().Be("original");
    }

    [Fact]
    public async Task A_cancelled_row_is_never_claimed_again_by_either_claim_path()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var message = new TestOutboxEventMessage
        {
            IdempotencyKey = "cancelled-then-claimed", EventType = "Debited", CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        context.OutboxEventMessages.Add(message);
        await context.SaveChangesAsync(ct);
        var claimed = await repository.TryClaimByIdAsync(message.Id, ct);
        await repository.MarkAsCancelledAsync(claimed!, "cancelled", ct);

        await using var otherContext = CreateContext();
        var otherRepository = new TestOutboxEventMessageRepository(otherContext, TimeProvider, TestBusinessMetrics.Instance);

        (await otherRepository.TryClaimByIdAsync(message.Id, ct)).Should().BeNull();
        (await otherRepository.TryClaimByIdIfOldestAsync(message.Id, partitionId: 0, ct)).Should().BeNull();
        (await otherRepository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct)).Should().BeEmpty();
        // Not even the stale-claim reclaim window revives it.
        TimeProvider.Advance(MessageConstants.Defaults.ProcessingTimeout + TimeSpan.FromMinutes(1));
        (await otherRepository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Is_withheld_when_a_concurrent_writer_reclaimed_the_row_as_processing()
    {
        // Boundary: never cancel a row that is Processing under someone else's
        // claim. The caller's tracked copy still says Pending, so its save
        // conflicts on the Status concurrency token; after the reload the row
        // is Processing and the cancel must be withheld, not re-applied.
        var ct = TestContext.Current.CancellationToken;
        await using var seedContext = CreateContext();
        var seeded = new TestInboxMessage { IdempotencyKey = "reclaimed", ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime };
        seedContext.InboxMessages.Add(seeded);
        await seedContext.SaveChangesAsync(ct);

        await using var cancellerContext = CreateContext();
        var canceller = new TestInboxMessageRepository(cancellerContext, TimeProvider, TestBusinessMetrics.Instance);
        var trackedByCanceller = await cancellerContext.InboxMessages.SingleAsync(m => m.Id == seeded.Id, ct);

        await using var claimerContext = CreateContext();
        var claimer = new TestInboxMessageRepository(claimerContext, TimeProvider, TestBusinessMetrics.Instance);
        (await claimer.TryClaimByIdAsync(seeded.Id, ct)).Should().NotBeNull();

        var outcome = await canceller.MarkAsCancelledAsync(trackedByCanceller, "cancel", ct);

        outcome.Should().Be(MessageTransitionOutcome.Conflicted);
        var reloaded = await seedContext.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == seeded.Id, ct);
        reloaded.Status.Should().Be(MessageConstants.Status.Processing, "the in-flight claim wins over the cancel");
        reloaded.ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task Is_withheld_and_never_re_applied_when_the_row_is_back_at_pending_after_a_conflict()
    {
        // Review finding: after a conflict the reload has discarded this
        // caller's cached-payload mutation, so re-applying the cancel would
        // persist a Cancelled row with no payload. Never re-applied: a row
        // that has been through another writer's hands reports Conflicted
        // even when it is Pending again. Reproduced deterministically: the
        // canceller claims (its tracked original becomes Processing), a
        // competitor stale-reclaims the row (Processing -> Processing, no
        // conflict for it) and releases it to Pending; the canceller's
        // UPDATE ... WHERE Status = 'Processing' then affects zero rows.
        var ct = TestContext.Current.CancellationToken;
        await using var seedContext = CreateContext();
        var seeded = new TestInboxMessage { IdempotencyKey = "back-to-pending", ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime };
        seedContext.InboxMessages.Add(seeded);
        await seedContext.SaveChangesAsync(ct);

        await using var cancellerContext = CreateContext();
        var canceller = new TestInboxMessageRepository(cancellerContext, TimeProvider, TestBusinessMetrics.Instance);
        var claimedByCanceller = await canceller.TryClaimByIdAsync(seeded.Id, ct);
        claimedByCanceller.Should().NotBeNull();

        await using var competitorContext = CreateContext();
        var competitor = new TestInboxMessageRepository(competitorContext, TimeProvider, TestBusinessMetrics.Instance);
        TimeProvider.Advance(MessageConstants.Defaults.ProcessingTimeout + TimeSpan.FromMinutes(1));
        var reclaimed = await competitor.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);
        reclaimed.Should().ContainSingle(m => m.Id == seeded.Id, "the stale claim is reclaimable");
        await competitor.MarkAsFailedWithRetryAsync(reclaimed[0], "released by the competitor", ct);

        var outcome = await canceller.MarkAsCancelledAsync(claimedByCanceller!, "cancel", ct);

        outcome.Should().Be(MessageTransitionOutcome.Conflicted);
        var reloaded = await seedContext.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == seeded.Id, ct);
        reloaded.Status.Should().Be(MessageConstants.Status.Pending, "the cancel is never re-applied after a conflict");
        reloaded.ProcessedAt.Should().BeNull();
        reloaded.LastError.Should().Be("released by the competitor");
    }

    [Fact]
    public async Task Reports_already_terminal_when_a_concurrent_writer_completed_the_row_first()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seedContext = CreateContext();
        var seeded = new TestInboxMessage { IdempotencyKey = "completed-first", ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime };
        seedContext.InboxMessages.Add(seeded);
        await seedContext.SaveChangesAsync(ct);

        await using var cancellerContext = CreateContext();
        var canceller = new TestInboxMessageRepository(cancellerContext, TimeProvider, TestBusinessMetrics.Instance);
        var trackedByCanceller = await cancellerContext.InboxMessages.SingleAsync(m => m.Id == seeded.Id, ct);

        await using var completerContext = CreateContext();
        var completer = new TestInboxMessageRepository(completerContext, TimeProvider, TestBusinessMetrics.Instance);
        var claimed = await completer.TryClaimByIdAsync(seeded.Id, ct);
        await completer.MarkAsCompletedAsync(claimed!, ct);

        var outcome = await canceller.MarkAsCancelledAsync(trackedByCanceller, "cancel", ct);

        outcome.Should().Be(MessageTransitionOutcome.AlreadyTerminal);
        var reloaded = await seedContext.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == seeded.Id, ct);
        reloaded.Status.Should().Be(MessageConstants.Status.Completed);
    }

    [Fact]
    public async Task Detached_message_from_a_different_context_is_attached_and_the_transition_persists()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seedContext = CreateContext();
        var seeded = new TestInboxMessage { IdempotencyKey = "detached-cancel", ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime };
        seedContext.InboxMessages.Add(seeded);
        await seedContext.SaveChangesAsync(ct);

        await using var loadContext = CreateContext();
        var detachedCopy = await loadContext.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == seeded.Id, ct);

        await using var repoContext = CreateContext();
        var repository = new TestInboxMessageRepository(repoContext, TimeProvider, TestBusinessMetrics.Instance);

        var outcome = await repository.MarkAsCancelledAsync(detachedCopy, "cancel", ct);

        outcome.Should().Be(MessageTransitionOutcome.Applied);
        var reloaded = await seedContext.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == seeded.Id, ct);
        reloaded.Status.Should().Be(MessageConstants.Status.Cancelled);
    }

    [Fact]
    public async Task Rejects_null_message_and_null_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var nullMessage = () => repository.MarkAsCancelledAsync(null!, "reason", ct);
        var nullReason = () => repository.MarkAsCancelledAsync(new TestInboxMessage { IdempotencyKey = "x" }, null!, ct);

        await nullMessage.Should().ThrowAsync<ArgumentNullException>();
        await nullReason.Should().ThrowAsync<ArgumentNullException>();
    }
}
