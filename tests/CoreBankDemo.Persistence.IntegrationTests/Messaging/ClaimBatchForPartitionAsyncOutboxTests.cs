using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Messaging;

/// <summary>
/// Outbox-side coverage for <c>ClaimBatchForPartitionAsync</c> (story 2.3):
/// <see cref="OutboxMessageRepositoryBase{TMessage,TDbContext}"/> implements
/// its own claimable-rows query and ordering-timestamp stamp keyed on
/// <see cref="IOutboxMessage.CreatedAt"/> rather than <c>ReceivedAt</c> — a
/// separate implementation from the inbox side (<see cref="ClaimBatchForPartitionAsyncTests"/>),
/// so it needs its own direct coverage rather than relying on the inbox tests.
/// </summary>
public class ClaimBatchForPartitionAsyncOutboxTests(PostgresContainerFixture fixture) : MessagingPostgresTestBase(fixture)
{
    [Fact]
    public async Task Claims_pending_outbox_row_oldest_first_and_transitions_to_processing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var oldest = new TestOutboxEventMessage
        {
            IdempotencyKey = "oldest", EventType = "Debited", CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        TimeProvider.Advance(TimeSpan.FromSeconds(1));
        var newest = new TestOutboxEventMessage
        {
            IdempotencyKey = "newest", EventType = "Credited", CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        context.OutboxEventMessages.AddRange(oldest, newest);
        await context.SaveChangesAsync(ct);

        var claimed = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 1, ct);

        claimed.Should().ContainSingle(m => m.Id == oldest.Id);
        claimed[0].Status.Should().Be(MessageConstants.Status.Processing);
    }

    [Fact]
    public async Task Excludes_poisoned_and_other_partition_outbox_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        context.OutboxEventMessages.Add(new TestOutboxEventMessage
        {
            IdempotencyKey = "poisoned", EventType = "Debited", RetryCount = MessageConstants.Defaults.MaxRetryCount,
        });
        context.OutboxEventMessages.Add(new TestOutboxEventMessage
        {
            IdempotencyKey = "other-partition", EventType = "Debited", PartitionId = 3,
        });
        await context.SaveChangesAsync(ct);

        var claimed = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);

        claimed.Should().BeEmpty();
    }

    [Fact]
    public async Task Stale_processing_outbox_row_is_reclaimed_and_not_immediately_reclaimable_again()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var stuck = new TestOutboxEventMessage
        {
            IdempotencyKey = "stuck",
            EventType = "Debited",
            Status = MessageConstants.Status.Processing,
            CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        context.OutboxEventMessages.Add(stuck);
        await context.SaveChangesAsync(ct);

        TimeProvider.Advance(MessageConstants.Defaults.ProcessingTimeout + TimeSpan.FromSeconds(1));

        var firstClaim = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);
        firstClaim.Should().ContainSingle(m => m.Id == stuck.Id);
        // Reclaiming must stamp CreatedAt forward (the story 2.3 staleness-basis
        // fix, mirrored from the inbox side) so it isn't immediately re-stale.
        var secondClaim = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);
        secondClaim.Should().BeEmpty();
    }

    [Fact]
    public async Task Fresh_processing_outbox_row_is_not_reclaimed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        context.OutboxEventMessages.Add(new TestOutboxEventMessage
        {
            IdempotencyKey = "in-flight",
            EventType = "Debited",
            Status = MessageConstants.Status.Processing,
            CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
        });
        await context.SaveChangesAsync(ct);

        TimeProvider.Advance(MessageConstants.Defaults.ProcessingTimeout - TimeSpan.FromSeconds(1));

        var claimed = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);

        claimed.Should().BeEmpty();
    }
}
