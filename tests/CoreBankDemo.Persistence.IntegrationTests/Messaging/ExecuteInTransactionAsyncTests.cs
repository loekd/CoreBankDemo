using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Messaging;

/// <summary>
/// <c>ExecuteInTransactionAsync</c> on <see cref="MessageRepositoryBase{TMessage,TDbContext}"/>
/// (story 2.3): commits a successful multi-step operation, and rolls back —
/// leaving no partial row change — when the operation throws partway through,
/// even after an inner <c>SaveChangesAsync</c> already ran.
/// </summary>
public class ExecuteInTransactionAsyncTests(PostgresContainerFixture fixture) : MessagingPostgresTestBase(fixture)
{
    [Fact]
    public async Task Successful_operation_commits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider);
        var message = new TestInboxMessage { IdempotencyKey = "commits" };

        await repository.ExecuteInTransactionAsync(async () =>
        {
            context.InboxMessages.Add(message);
            await context.SaveChangesAsync(ct);
        }, ct);

        await using var verifyContext = CreateContext();
        (await verifyContext.InboxMessages.CountAsync(m => m.Id == message.Id, ct)).Should().Be(1);
    }

    [Fact]
    public async Task Operation_throwing_after_a_save_leaves_no_partial_state()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider);
        var message = new TestInboxMessage { IdempotencyKey = "rolled-back" };

        var act = async () => await repository.ExecuteInTransactionAsync(async () =>
        {
            context.InboxMessages.Add(message);
            await context.SaveChangesAsync(ct);
            throw new InvalidOperationException("boom");
        }, ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        await using var verifyContext = CreateContext();
        (await verifyContext.InboxMessages.CountAsync(m => m.Id == message.Id, ct)).Should().Be(0,
            "the throw happened inside the same transaction as the save, so the save must not have persisted");
    }

    [Fact]
    public async Task Operation_throwing_partway_through_a_multi_row_update_leaves_no_partial_state()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider);

        var first = new TestInboxMessage { IdempotencyKey = "multi-row-1" };
        var second = new TestInboxMessage { IdempotencyKey = "multi-row-2" };
        context.InboxMessages.AddRange(first, second);
        await context.SaveChangesAsync(ct);

        var act = async () => await repository.ExecuteInTransactionAsync(async () =>
        {
            first.Status = MessageConstants.Status.Processing;
            await context.SaveChangesAsync(ct);

            second.Status = MessageConstants.Status.Processing;
            throw new InvalidOperationException("boom partway through the second row");
        }, ct);

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var verifyContext = CreateContext();
        var reloadedFirst = await verifyContext.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == first.Id, ct);
        var reloadedSecond = await verifyContext.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == second.Id, ct);

        reloadedFirst.Status.Should().Be(MessageConstants.Status.Pending, "the first row's save must have rolled back too");
        reloadedSecond.Status.Should().Be(MessageConstants.Status.Pending);
    }

    [Fact]
    public async Task Rejects_null_operation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider);

        var act = async () => await repository.ExecuteInTransactionAsync(null!, ct);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("operation");
    }
}
