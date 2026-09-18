using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Messaging;

/// <summary>
/// A handler may commit a claimed row as <c>Completed</c> through its own
/// <see cref="DbContext"/> while the claiming context still tracks the same
/// instance with a <c>Processing</c> snapshot (CoreBank's
/// <c>TransactionExecutionHandler</c> does exactly this). The claiming context
/// must not then resend a stale <c>UPDATE ... WHERE "Status" = 'Processing'</c>
/// for that row with every later save: the retry of the next row and the
/// release of the rows behind it would both fail on a row that is not theirs
/// (ADR-023 final review).
/// </summary>
public class HandlerCommittedRowTests(PostgresContainerFixture fixture) : MessagingPostgresTestBase(fixture)
{
    [Fact]
    public async Task A_retry_is_still_recorded_after_another_context_completed_an_earlier_claimed_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var claimingContext = CreateContext();
        var repository = new TestInboxMessageRepository(claimingContext, TimeProvider, TestBusinessMetrics.Instance);
        var (first, second) = await ClaimTwoRowsAsync(repository, ct);
        await CompleteThroughAnotherContextAsync(first, ct);

        var completion = await repository.MarkAsCompletedAsync(first, ct);
        var retry = await repository.MarkAsFailedWithRetryAsync(second, "boom", ct);

        completion.Should().Be(MessageTransitionOutcome.AlreadyTerminal);
        retry.Should().Be(MessageTransitionOutcome.Applied);
        await using var verify = CreateContext();
        var persistedFirst = await verify.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == first.Id, ct);
        var persistedSecond = await verify.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == second.Id, ct);
        persistedFirst.Status.Should().Be(MessageConstants.Status.Completed);
        persistedSecond.Status.Should().Be(MessageConstants.Status.Pending);
        persistedSecond.RetryCount.Should().Be(1);
        persistedSecond.LastError.Should().Be("boom");
    }

    [Fact]
    public async Task A_claim_is_still_released_after_another_context_completed_an_earlier_claimed_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var claimingContext = CreateContext();
        var repository = new TestInboxMessageRepository(claimingContext, TimeProvider, TestBusinessMetrics.Instance);
        var (first, second) = await ClaimTwoRowsAsync(repository, ct);
        await CompleteThroughAnotherContextAsync(first, ct);

        var completion = await repository.MarkAsCompletedAsync(first, ct);
        await repository.ReleaseClaimsAsync([second], ct);

        completion.Should().Be(MessageTransitionOutcome.AlreadyTerminal);
        await using var verify = CreateContext();
        var persistedFirst = await verify.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == first.Id, ct);
        var persistedSecond = await verify.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == second.Id, ct);
        persistedFirst.Status.Should().Be(MessageConstants.Status.Completed);
        persistedSecond.Status.Should().Be(MessageConstants.Status.Pending);
        persistedSecond.RetryCount.Should().Be(0);
        persistedSecond.LastError.Should().BeNull();
    }

    /// <summary>
    /// The same poisoning without the <c>MarkAsCompletedAsync</c> call that
    /// stops tracking the completed row: the conflict the save reports is on
    /// the completed row, not on the row in hand, so that stale pending change
    /// is dropped and the retry is saved again.
    /// </summary>
    [Fact]
    public async Task A_retry_is_still_recorded_while_a_row_another_context_completed_is_still_tracked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var claimingContext = CreateContext();
        var repository = new TestInboxMessageRepository(claimingContext, TimeProvider, TestBusinessMetrics.Instance);
        var (first, second) = await ClaimTwoRowsAsync(repository, ct);
        await CompleteThroughAnotherContextAsync(first, ct);

        var retry = await repository.MarkAsFailedWithRetryAsync(second, "boom", ct);

        retry.Should().Be(MessageTransitionOutcome.Applied);
        await using var verify = CreateContext();
        var persistedFirst = await verify.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == first.Id, ct);
        var persistedSecond = await verify.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == second.Id, ct);
        persistedFirst.Status.Should().Be(MessageConstants.Status.Completed);
        persistedSecond.Status.Should().Be(MessageConstants.Status.Pending);
        persistedSecond.RetryCount.Should().Be(1);
        persistedSecond.LastError.Should().Be("boom");
    }

    [Fact]
    public async Task A_claim_is_still_released_while_a_row_another_context_completed_is_still_tracked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var claimingContext = CreateContext();
        var repository = new TestInboxMessageRepository(claimingContext, TimeProvider, TestBusinessMetrics.Instance);
        var (first, second) = await ClaimTwoRowsAsync(repository, ct);
        await CompleteThroughAnotherContextAsync(first, ct);

        await repository.ReleaseClaimsAsync([second], ct);

        await using var verify = CreateContext();
        var persistedFirst = await verify.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == first.Id, ct);
        var persistedSecond = await verify.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == second.Id, ct);
        persistedFirst.Status.Should().Be(MessageConstants.Status.Completed);
        persistedSecond.Status.Should().Be(MessageConstants.Status.Pending);
        persistedSecond.RetryCount.Should().Be(0);
    }

    /// <summary>
    /// A release is retried once. When the retried save conflicts on yet
    /// another row, the row in hand is left <c>Processing</c> for stale reclaim
    /// rather than forced, nothing is thrown, and the next row still releases.
    /// </summary>
    [Fact]
    public async Task A_release_that_conflicts_on_other_rows_twice_leaves_its_row_processing_and_releases_the_next()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var claimingContext = CreateContext();
        var repository = new TestInboxMessageRepository(claimingContext, TimeProvider, TestBusinessMetrics.Instance);
        var claimed = await ClaimRowsAsync(repository, 4, ct);
        await CompleteThroughAnotherContextAsync(claimed[0], ct);
        await CompleteThroughAnotherContextAsync(claimed[1], ct);

        var act = () => repository.ReleaseClaimsAsync([claimed[2], claimed[3]], ct);

        await act.Should().NotThrowAsync();
        await using var verify = CreateContext();
        var statuses = await verify.InboxMessages.AsNoTracking()
            .ToDictionaryAsync(m => m.IdempotencyKey, m => m.Status, ct);
        statuses["row-1"].Should().Be(MessageConstants.Status.Completed);
        statuses["row-2"].Should().Be(MessageConstants.Status.Completed);
        statuses["row-3"].Should().Be(MessageConstants.Status.Processing);
        statuses["row-4"].Should().Be(MessageConstants.Status.Pending);
        claimed[2].Status.Should().Be(MessageConstants.Status.Processing, "the instance takes the database's values");
    }

    private async Task<(TestInboxMessage First, TestInboxMessage Second)> ClaimTwoRowsAsync(
        TestInboxMessageRepository repository, CancellationToken ct)
    {
        var claimed = await ClaimRowsAsync(repository, 2, ct);
        return (claimed[0], claimed[1]);
    }

    private async Task<IReadOnlyList<TestInboxMessage>> ClaimRowsAsync(
        TestInboxMessageRepository repository, int count, CancellationToken ct)
    {
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        await using (var seed = CreateContext())
        {
            seed.InboxMessages.AddRange(Enumerable.Range(1, count).Select(n =>
                new TestInboxMessage { IdempotencyKey = $"row-{n}", ReceivedAt = now.AddSeconds(n - count - 1) }));
            await seed.SaveChangesAsync(ct);
        }

        var claimed = await repository.ClaimBatchForPartitionAsync(0, 10, ct);
        claimed.Select(m => m.IdempotencyKey).Should().Equal(Enumerable.Range(1, count).Select(n => $"row-{n}"));
        return claimed;
    }

    /// <summary>
    /// What a handler with its own scoped context does: it attaches the very
    /// instance the claiming context tracks and commits it as <c>Completed</c>.
    /// </summary>
    private async Task CompleteThroughAnotherContextAsync(TestInboxMessage message, CancellationToken ct)
    {
        await using var handlerContext = CreateContext();
        handlerContext.Attach(message);
        message.Status = MessageConstants.Status.Completed;
        message.ProcessedAt = TimeProvider.GetUtcNow().UtcDateTime;
        await handlerContext.SaveChangesAsync(ct);
    }
}
