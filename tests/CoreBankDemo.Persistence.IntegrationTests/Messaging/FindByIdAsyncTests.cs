using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Messaging;

/// <summary>
/// <c>FindByIdAsync</c> on <see cref="MessageRepositoryBase{TMessage,TDbContext}"/>
/// (story 2.4): a plain by-id lookup, added alongside <c>MarkAsCompletedAsync</c>
/// per the story's code map. Not part of <see cref="IOutboxMessageStore{TMessage}"/>
/// (the port stays narrow — <see cref="OutboxProcessorBase{TMessage}"/> works
/// directly off what <c>ClaimBatchForPartitionAsync</c> already returns rather
/// than re-loading each message by id), but a repository-level capability
/// future stories (e.g. inbox handler dispatch, story 2.5) can build on.
/// </summary>
public class FindByIdAsyncTests(PostgresContainerFixture fixture) : MessagingPostgresTestBase(fixture)
{
    [Fact]
    public async Task Returns_the_message_when_it_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider);

        var message = new TestOutboxEventMessage { IdempotencyKey = "find-me", EventType = "Debited" };
        context.OutboxEventMessages.Add(message);
        await context.SaveChangesAsync(ct);

        var found = await repository.FindByIdAsync(message.Id, ct);

        found.Should().NotBeNull();
        found!.Id.Should().Be(message.Id);
        found.IdempotencyKey.Should().Be("find-me");
    }

    [Fact]
    public async Task Returns_null_when_no_row_has_that_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider);

        var found = await repository.FindByIdAsync(Guid.NewGuid(), ct);

        found.Should().BeNull();
    }
}
