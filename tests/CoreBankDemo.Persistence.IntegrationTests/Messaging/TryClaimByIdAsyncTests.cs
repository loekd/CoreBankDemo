using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Messaging;

/// <summary>
/// Proves <c>MessageRepositoryBase&lt;TMessage,TDbContext&gt;.TryClaimByIdAsync</c>
/// against real PostgreSQL (spec: add-instant-payment-rail's inline claim
/// path): a fresh <c>Pending</c> row claims and transitions to
/// <c>Processing</c>; a row already <c>Processing</c>, <c>Completed</c>, or
/// <c>Failed</c> is reported not-claimable; a concurrent claimer racing this
/// call (simulated via <see cref="OutboxMessageRepositoryBase{TMessage,TDbContext}.ClaimBatchForPartitionAsync"/>
/// against the same row from a second context) can never both win.
/// </summary>
public class TryClaimByIdAsyncTests(PostgresContainerFixture fixture) : MessagingPostgresTestBase(fixture)
{
    [Fact]
    public async Task Claims_a_pending_row_and_transitions_it_to_processing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var message = new TestOutboxEventMessage
        {
            IdempotencyKey = "claim-me", EventType = "Debited", CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        context.OutboxEventMessages.Add(message);
        await context.SaveChangesAsync(ct);

        var claimed = await repository.TryClaimByIdAsync(message.Id, ct);

        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(message.Id);
        claimed.Status.Should().Be(MessageConstants.Status.Processing);
    }

    [Fact]
    public async Task Returns_null_for_an_unknown_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var claimed = await repository.TryClaimByIdAsync(Guid.NewGuid(), ct);

        claimed.Should().BeNull();
    }

    [Theory]
    [InlineData(MessageConstants.Status.Processing)]
    [InlineData(MessageConstants.Status.Completed)]
    [InlineData(MessageConstants.Status.Failed)]
    public async Task Returns_null_for_a_row_that_is_not_pending(string status)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var message = new TestOutboxEventMessage
        {
            IdempotencyKey = "not-pending",
            EventType = "Debited",
            Status = status,
            CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        context.OutboxEventMessages.Add(message);
        await context.SaveChangesAsync(ct);

        var claimed = await repository.TryClaimByIdAsync(message.Id, ct);

        claimed.Should().BeNull();
        // Never mutated -- still exactly the pre-existing status.
        var reloaded = await repository.FindByIdAsync(message.Id, ct);
        reloaded!.Status.Should().Be(status);
    }

    [Fact]
    public async Task A_row_claimed_inline_is_no_longer_claimable_by_a_background_batch_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seedContext = CreateContext();
        var message = new TestOutboxEventMessage
        {
            IdempotencyKey = "inline-first", EventType = "Debited", CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        seedContext.OutboxEventMessages.Add(message);
        await seedContext.SaveChangesAsync(ct);

        await using var inlineContext = CreateContext();
        await using var backgroundContext = CreateContext();
        var inlineRepository = new TestOutboxEventMessageRepository(inlineContext, TimeProvider, TestBusinessMetrics.Instance);
        var backgroundRepository = new TestOutboxEventMessageRepository(backgroundContext, TimeProvider, TestBusinessMetrics.Instance);

        var inlineClaim = await inlineRepository.TryClaimByIdAsync(message.Id, ct);
        var backgroundClaim = await backgroundRepository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);

        inlineClaim.Should().NotBeNull();
        backgroundClaim.Should().NotContain(m => m.Id == message.Id);
    }

    [Fact]
    public async Task A_row_claimed_by_a_background_batch_claim_is_no_longer_claimable_inline()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seedContext = CreateContext();
        var message = new TestOutboxEventMessage
        {
            IdempotencyKey = "background-first", EventType = "Debited", CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        seedContext.OutboxEventMessages.Add(message);
        await seedContext.SaveChangesAsync(ct);

        await using var inlineContext = CreateContext();
        await using var backgroundContext = CreateContext();
        var inlineRepository = new TestOutboxEventMessageRepository(inlineContext, TimeProvider, TestBusinessMetrics.Instance);
        var backgroundRepository = new TestOutboxEventMessageRepository(backgroundContext, TimeProvider, TestBusinessMetrics.Instance);

        var backgroundClaim = await backgroundRepository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);
        var inlineClaim = await inlineRepository.TryClaimByIdAsync(message.Id, ct);

        backgroundClaim.Should().ContainSingle(m => m.Id == message.Id);
        inlineClaim.Should().BeNull();
    }

    [Fact]
    public async Task Rejects_an_empty_guid()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var act = () => repository.TryClaimByIdAsync(Guid.Empty, ct);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Ordered_claim_refuses_to_overtake_an_earlier_pending_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var earlier = new TestOutboxEventMessage
        {
            IdempotencyKey = "earlier",
            EventType = "Debited",
            PartitionId = 2,
            CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        var later = new TestOutboxEventMessage
        {
            IdempotencyKey = "later",
            EventType = "Debited",
            PartitionId = 2,
            CreatedAt = TimeProvider.GetUtcNow().UtcDateTime.AddSeconds(1),
        };
        context.AddRange(earlier, later);
        await context.SaveChangesAsync(ct);

        var claimed = await repository.TryClaimByIdIfOldestAsync(later.Id, 2, ct);

        claimed.Should().BeNull();
        (await repository.FindByIdAsync(later.Id, ct))!.Status.Should().Be(MessageConstants.Status.Pending);
    }

    [Fact]
    public async Task Ordered_claim_claims_the_oldest_pending_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var message = new TestOutboxEventMessage
        {
            IdempotencyKey = "oldest",
            EventType = "Debited",
            PartitionId = 3,
            CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        context.Add(message);
        await context.SaveChangesAsync(ct);

        var claimed = await repository.TryClaimByIdIfOldestAsync(message.Id, 3, ct);

        claimed.Should().NotBeNull();
        claimed!.Status.Should().Be(MessageConstants.Status.Processing);
    }

    [Fact]
    public async Task Ordered_claim_lets_a_higher_priority_row_overtake_an_earlier_standard_row()
    {
        // ADR-018 priority addendum: an SCT Inst never waits behind queued
        // batch (SCT) work in its partition.
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var earlierStandard = new TestOutboxEventMessage
        {
            IdempotencyKey = "earlier-standard", EventType = "Debited", PartitionId = 2,
            Priority = MessageConstants.Priority.Standard, CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        var laterInstant = new TestOutboxEventMessage
        {
            IdempotencyKey = "later-instant", EventType = "Debited", PartitionId = 2,
            Priority = MessageConstants.Priority.Instant, CreatedAt = TimeProvider.GetUtcNow().UtcDateTime.AddSeconds(1),
        };
        context.AddRange(earlierStandard, laterInstant);
        await context.SaveChangesAsync(ct);

        var claimed = await repository.TryClaimByIdIfOldestAsync(laterInstant.Id, 2, ct);

        claimed.Should().NotBeNull();
        claimed!.Status.Should().Be(MessageConstants.Status.Processing);
        (await repository.FindByIdAsync(earlierStandard.Id, ct))!.Status.Should().Be(MessageConstants.Status.Pending);
    }

    [Fact]
    public async Task Ordered_claim_still_refuses_to_overtake_an_earlier_row_of_the_same_priority()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var earlierInstant = new TestOutboxEventMessage
        {
            IdempotencyKey = "earlier-instant", EventType = "Debited", PartitionId = 2,
            Priority = MessageConstants.Priority.Instant, CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        var laterInstant = new TestOutboxEventMessage
        {
            IdempotencyKey = "later-instant", EventType = "Debited", PartitionId = 2,
            Priority = MessageConstants.Priority.Instant, CreatedAt = TimeProvider.GetUtcNow().UtcDateTime.AddSeconds(1),
        };
        context.AddRange(earlierInstant, laterInstant);
        await context.SaveChangesAsync(ct);

        (await repository.TryClaimByIdIfOldestAsync(laterInstant.Id, 2, ct)).Should().BeNull();
    }

    [Fact]
    public async Task Ordered_claim_ignores_the_hold_it_exists_for()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        var held = new TestOutboxEventMessage
        {
            IdempotencyKey = "held", EventType = "E", PartitionId = 3, CreatedAt = now, HoldUntil = now.AddSeconds(9),
        };
        context.Add(held);
        await context.SaveChangesAsync(ct);

        var claimed = await repository.TryClaimByIdIfOldestAsync(held.Id, 3, ct);

        claimed.Should().NotBeNull();
        claimed!.Status.Should().Be(MessageConstants.Status.Processing);
    }

    [Fact]
    public async Task Ordered_claim_rejects_empty_id()
    {
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var act = () => repository.TryClaimByIdIfOldestAsync(
            Guid.Empty,
            0,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Ordered_claim_returns_null_when_partition_has_no_claimable_rows()
    {
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var claimed = await repository.TryClaimByIdIfOldestAsync(
            Guid.NewGuid(),
            0,
            TestContext.Current.CancellationToken);

        claimed.Should().BeNull();
    }

    [Fact]
    public async Task Ordered_claim_does_not_take_a_stale_processing_row_inline()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var message = new TestOutboxEventMessage
        {
            IdempotencyKey = "stale-processing",
            EventType = "Debited",
            PartitionId = 1,
            Status = MessageConstants.Status.Processing,
            CreatedAt = TimeProvider.GetUtcNow().UtcDateTime - MessageConstants.Defaults.ProcessingTimeout - TimeSpan.FromSeconds(1),
        };
        context.Add(message);
        await context.SaveChangesAsync(ct);

        var claimed = await repository.TryClaimByIdIfOldestAsync(message.Id, 1, ct);

        claimed.Should().BeNull();
        (await repository.FindByIdAsync(message.Id, ct))!.Status.Should().Be(MessageConstants.Status.Processing);
    }
}
