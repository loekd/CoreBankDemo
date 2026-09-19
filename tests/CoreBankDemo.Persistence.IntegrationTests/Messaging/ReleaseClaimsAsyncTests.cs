using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Messaging;

/// <summary>
/// <c>ReleaseClaimsAsync</c> (ADR-023): when a batch stops at its first failed
/// row, the rows claimed behind it go back to <c>Pending</c> exactly as they
/// were -- no retry counted, no error recorded -- so the failed row is first
/// in line again on the next tick and nothing overtakes it.
/// </summary>
public class ReleaseClaimsAsyncTests(PostgresContainerFixture fixture) : MessagingPostgresTestBase(fixture)
{
    [Fact]
    public async Task Processing_rows_return_to_pending_with_retry_count_and_error_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var first = new TestInboxMessage { IdempotencyKey = "r-1", Status = MessageConstants.Status.Processing, RetryCount = 3, LastError = "earlier" };
        var second = new TestInboxMessage { IdempotencyKey = "r-2", Status = MessageConstants.Status.Processing };
        context.InboxMessages.AddRange(first, second);
        await context.SaveChangesAsync(ct);

        await repository.ReleaseClaimsAsync([first, second], ct);

        var reloaded = await context.InboxMessages.AsNoTracking().OrderBy(m => m.IdempotencyKey).ToListAsync(ct);
        reloaded.Should().AllSatisfy(m => m.Status.Should().Be(MessageConstants.Status.Pending));
        reloaded[0].RetryCount.Should().Be(3);
        reloaded[0].LastError.Should().Be("earlier");
        reloaded[1].RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task Rows_that_are_not_processing_are_left_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var completed = new TestInboxMessage { IdempotencyKey = "done", Status = MessageConstants.Status.Completed };
        var cancelled = new TestInboxMessage { IdempotencyKey = "gone", Status = MessageConstants.Status.Cancelled };
        context.InboxMessages.AddRange(completed, cancelled);
        await context.SaveChangesAsync(ct);

        await repository.ReleaseClaimsAsync([completed, cancelled], ct);

        var statuses = await context.InboxMessages.AsNoTracking().Select(m => m.Status).ToListAsync(ct);
        statuses.Should().BeEquivalentTo([MessageConstants.Status.Completed, MessageConstants.Status.Cancelled]);
    }

    [Fact]
    public async Task A_row_another_writer_already_moved_is_skipped_and_the_rest_still_release()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var raced = new TestInboxMessage { IdempotencyKey = "raced", Status = MessageConstants.Status.Processing };
        var plain = new TestInboxMessage { IdempotencyKey = "plain", Status = MessageConstants.Status.Processing };
        context.InboxMessages.AddRange(raced, plain);
        await context.SaveChangesAsync(ct);

        // Another writer cancels the first row behind this context's back.
        await using (var other = CreateContext())
        {
            var theirs = await other.InboxMessages.SingleAsync(m => m.Id == raced.Id, ct);
            theirs.Status = MessageConstants.Status.Cancelled;
            await other.SaveChangesAsync(ct);
        }

        var act = () => repository.ReleaseClaimsAsync([raced, plain], ct);

        await act.Should().NotThrowAsync();
        await using var verify = CreateContext();
        (await verify.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == raced.Id, ct))
            .Status.Should().Be(MessageConstants.Status.Cancelled, "a conflicting row is never forced");
        (await verify.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == plain.Id, ct))
            .Status.Should().Be(MessageConstants.Status.Pending);
    }

    [Fact]
    public async Task A_row_loaded_by_a_different_context_is_attached_and_released()
    {
        var ct = TestContext.Current.CancellationToken;
        TestInboxMessage claimedElsewhere;
        await using (var other = CreateContext())
        {
            claimedElsewhere = new TestInboxMessage { IdempotencyKey = "elsewhere", Status = MessageConstants.Status.Processing, RetryCount = 2 };
            other.InboxMessages.Add(claimedElsewhere);
            await other.SaveChangesAsync(ct);
        }

        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        context.Entry(claimedElsewhere).State.Should().Be(EntityState.Detached);

        await repository.ReleaseClaimsAsync([claimedElsewhere], ct);

        await using var verify = CreateContext();
        var persisted = await verify.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == claimedElsewhere.Id, ct);
        persisted.Status.Should().Be(MessageConstants.Status.Pending);
        persisted.RetryCount.Should().Be(2);
    }

    [Fact]
    public async Task Rejects_a_null_list()
    {
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var act = () => repository.ReleaseClaimsAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
