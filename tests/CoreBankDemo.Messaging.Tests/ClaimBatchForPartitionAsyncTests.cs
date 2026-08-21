using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Messaging.Tests;

/// <summary>
/// <c>ClaimBatchForPartitionAsync</c> on <see cref="InboxMessageRepositoryBase{TMessage,TDbContext}"/>
/// / <see cref="OutboxMessageRepositoryBase{TMessage,TDbContext}"/> (story 2.3):
/// the full I/O matrix — batch size and ordering, poison exclusion, partition
/// isolation, stale-Processing reclaim (measured from claim time, not
/// creation/receipt time — the legacy violation this story fixes), fresh
/// Processing exclusion, and disjoint concurrent claims — against the SQLite
/// store test tier (AD-9).
/// </summary>
public class ClaimBatchForPartitionAsyncTests : SqliteMessagingTestBase
{
    [Fact]
    public async Task Claims_exactly_batchSize_oldest_first_and_transitions_to_processing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider);

        var oldest = new TestInboxMessage { IdempotencyKey = "oldest", ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime };
        TimeProvider.Advance(TimeSpan.FromSeconds(1));
        var middle = new TestInboxMessage { IdempotencyKey = "middle", ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime };
        TimeProvider.Advance(TimeSpan.FromSeconds(1));
        var newest = new TestInboxMessage { IdempotencyKey = "newest", ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime };

        context.InboxMessages.AddRange(oldest, middle, newest);
        await context.SaveChangesAsync(ct);

        var claimed = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 2, ct);

        claimed.Should().HaveCount(2);
        claimed[0].Id.Should().Be(oldest.Id);
        claimed[1].Id.Should().Be(middle.Id);
        claimed.Should().OnlyContain(m => m.Status == MessageConstants.Status.Processing);

        (await context.InboxMessages.SingleAsync(m => m.Id == newest.Id, ct)).Status
            .Should().Be(MessageConstants.Status.Pending, "batchSize excludes the third-oldest row");
    }

    [Fact]
    public async Task Excludes_poisoned_rows_at_max_retry_count()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider);

        var poisoned = new TestInboxMessage
        {
            IdempotencyKey = "poisoned",
            RetryCount = MessageConstants.Defaults.MaxRetryCount,
        };
        context.InboxMessages.Add(poisoned);
        await context.SaveChangesAsync(ct);

        var claimed = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);

        claimed.Should().BeEmpty();
    }

    [Fact]
    public async Task Excludes_rows_in_other_partitions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider);

        context.InboxMessages.Add(new TestInboxMessage { IdempotencyKey = "partition-2-row", PartitionId = 2 });
        await context.SaveChangesAsync(ct);

        var claimed = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);

        claimed.Should().BeEmpty();
    }

    [Fact]
    public async Task Stale_processing_row_is_reclaimed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider);

        var stuck = new TestInboxMessage
        {
            IdempotencyKey = "stuck",
            Status = MessageConstants.Status.Processing,
            ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        context.InboxMessages.Add(stuck);
        await context.SaveChangesAsync(ct);

        TimeProvider.Advance(MessageConstants.Defaults.ProcessingTimeout + TimeSpan.FromSeconds(1));

        var claimed = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);

        claimed.Should().ContainSingle(m => m.Id == stuck.Id);
    }

    [Fact]
    public async Task Fresh_processing_row_is_not_reclaimed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider);

        var inFlight = new TestInboxMessage
        {
            IdempotencyKey = "in-flight",
            Status = MessageConstants.Status.Processing,
            ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        context.InboxMessages.Add(inFlight);
        await context.SaveChangesAsync(ct);

        TimeProvider.Advance(MessageConstants.Defaults.ProcessingTimeout - TimeSpan.FromSeconds(1));

        var claimed = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);

        claimed.Should().BeEmpty();
    }

    [Fact]
    public async Task Reclaimed_stale_row_is_not_immediately_reclaimable_again()
    {
        // Proves the story 2.3 staleness-basis fix: the legacy kernel measured
        // staleness from the row's original receipt time, so a message that had
        // simply been sitting in the queue for a while looked "stale" the
        // instant it was legitimately (re-)claimed. Claiming must stamp the
        // ordering timestamp forward so an immediate follow-up claim call does
        // NOT re-grab the same row out from under whoever just claimed it.
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider);

        var stuck = new TestInboxMessage
        {
            IdempotencyKey = "stuck",
            Status = MessageConstants.Status.Processing,
            ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime,
        };
        context.InboxMessages.Add(stuck);
        await context.SaveChangesAsync(ct);

        TimeProvider.Advance(MessageConstants.Defaults.ProcessingTimeout + TimeSpan.FromSeconds(1));
        var firstClaim = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);
        firstClaim.Should().ContainSingle(m => m.Id == stuck.Id);

        var secondClaim = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);

        secondClaim.Should().BeEmpty();
    }

    [Fact]
    public async Task Reclaimed_stale_row_preserves_original_arrival_order_relative_to_a_later_arrival()
    {
        // Proves the ordering-bug fix: SetOrderingTimestamp must only stamp
        // forward for rows that were ALREADY Processing before this claim call
        // (a true stale-claim reclaim) - never for rows claimed fresh from
        // Pending. Before the fix, A's very first claim (Pending -> Processing)
        // already overwrote its ReceivedAt to that claim's instant; if that
        // instant lands after a message which genuinely arrived later (B
        // here), the corrupted timestamp makes A look newer than B at the next
        // reclaim - silently violating the per-partition oldest-first FIFO
        // guarantee (AD-4) across separate claim calls, even though any single
        // claim call's own SELECT still sorts correctly on whatever is
        // currently stored at that instant.
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider);

        var originalArrival = TimeProvider.GetUtcNow().UtcDateTime;
        var messageA = new TestInboxMessage { IdempotencyKey = "fifo-a", ReceivedAt = originalArrival };
        context.InboxMessages.Add(messageA);
        await context.SaveChangesAsync(ct);

        // A isn't picked up immediately - simulate a pickup delay before its
        // first claim, so a buggy stamp-on-every-claim would visibly move its
        // ordering timestamp forward from its true arrival.
        TimeProvider.Advance(TimeSpan.FromMinutes(2));
        var firstClaim = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);
        firstClaim.Should().ContainSingle(m => m.Id == messageA.Id);

        // A crashes and is never completed. B arrives next, genuinely after A:
        // its ReceivedAt sits between A's true original arrival and the
        // instant A was first claimed.
        var messageB = new TestInboxMessage
        {
            IdempotencyKey = "fifo-b",
            ReceivedAt = originalArrival + TimeSpan.FromMinutes(1),
        };
        context.InboxMessages.Add(messageB);
        await context.SaveChangesAsync(ct);

        // Advance well past ProcessingTimeout so A is unambiguously
        // stale-reclaimable regardless of which ReceivedAt value is currently
        // stored for it.
        TimeProvider.Advance(MessageConstants.Defaults.ProcessingTimeout + TimeSpan.FromMinutes(1));

        var secondClaim = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);

        secondClaim.Should().HaveCount(2);
        secondClaim[0].Id.Should().Be(messageA.Id,
            "A's true arrival order must survive being reclaimed after going stale, not be reset to look newer than B");
        secondClaim[1].Id.Should().Be(messageB.Id);
    }

    [Fact]
    public async Task Rejects_negative_partitionId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider);

        var act = async () => await repository.ClaimBatchForPartitionAsync(partitionId: -1, batchSize: 10, ct);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("partitionId");
    }

    [Fact]
    public async Task Concurrent_claims_on_same_partition_produce_disjoint_sets()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var seedContext = CreateContext())
        {
            for (var i = 0; i < 15; i++)
            {
                // ReceivedAt must reflect a realistic arrival time (as the
                // caller who ingests a message would set it) - the story 2.3
                // fix no longer stamps it forward on a row's first, fresh
                // claim, so a row left at its unset DateTime.MinValue default
                // would look immediately (and wrongly) stale-reclaimable to
                // any claim call that runs right after this one commits.
                seedContext.InboxMessages.Add(new TestInboxMessage
                {
                    IdempotencyKey = $"concurrent-{i}",
                    ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime,
                });
            }

            await seedContext.SaveChangesAsync(ct);
        }

        async Task<List<TestInboxMessage>> ClaimAsync()
        {
            await using var context = CreateContext();
            var repository = new TestInboxMessageRepository(context, TimeProvider);
            var claimed = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);
            return [.. claimed];
        }

        var act = async () => await Task.WhenAll(ClaimAsync(), ClaimAsync());
        var results = await act.Should().NotThrowAsync();

        var (claimedByA, claimedByB) = (results.Which[0], results.Which[1]);
        var idsA = claimedByA.Select(m => m.Id).ToHashSet();
        var idsB = claimedByB.Select(m => m.Id).ToHashSet();

        idsA.Intersect(idsB).Should().BeEmpty("the same row must never be claimed by both concurrent callers");

        await using var verifyContext = CreateContext();
        var processingCount = await verifyContext.InboxMessages
            .CountAsync(m => m.Status == MessageConstants.Status.Processing, ct);
        processingCount.Should().Be(idsA.Count + idsB.Count);
    }

    [Fact]
    public async Task Concurrent_claim_of_a_single_contested_row_leaves_it_claimed_by_exactly_one_caller()
    {
        // A single candidate maximizes the odds both concurrent calls read it
        // before either writes, so the loser's SaveChangesAsync hits the
        // optimistic-concurrency check and ClaimBatchForPartitionAsync's
        // conflict-retry path (drop the losing entry, retry with what's left)
        // runs for real rather than by scheduling luck.
        var ct = TestContext.Current.CancellationToken;

        Guid contestedId;
        await using (var seedContext = CreateContext())
        {
            // See the comment in Concurrent_claims_on_same_partition_produce_disjoint_sets:
            // ReceivedAt must be realistic, not the DateTime.MinValue default,
            // or the row would look immediately stale-reclaimable right after
            // its first (fresh) claim under the story 2.3 ordering fix.
            var seeded = new TestInboxMessage
            {
                IdempotencyKey = "single-contested",
                ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime,
            };
            seedContext.InboxMessages.Add(seeded);
            await seedContext.SaveChangesAsync(ct);
            contestedId = seeded.Id;
        }

        async Task<List<TestInboxMessage>> ClaimAsync()
        {
            await using var context = CreateContext();
            var repository = new TestInboxMessageRepository(context, TimeProvider);
            var claimed = await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 1, ct);
            return [.. claimed];
        }

        var act = async () => await Task.WhenAll(ClaimAsync(), ClaimAsync());
        var results = await act.Should().NotThrowAsync();

        var (claimedByA, claimedByB) = (results.Which[0], results.Which[1]);
        (claimedByA.Count + claimedByB.Count).Should().Be(1, "exactly one caller may win the single row, the other must lose gracefully");
        (claimedByA.Any(m => m.Id == contestedId) ^ claimedByB.Any(m => m.Id == contestedId)).Should().BeTrue();

        await using var verifyContext = CreateContext();
        var reloaded = await verifyContext.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == contestedId, ct);
        reloaded.Status.Should().Be(MessageConstants.Status.Processing);
    }

    [Fact]
    public async Task Rejects_non_positive_batchSize()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider);

        var act = async () => await repository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 0, ct);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("batchSize");
    }
}
