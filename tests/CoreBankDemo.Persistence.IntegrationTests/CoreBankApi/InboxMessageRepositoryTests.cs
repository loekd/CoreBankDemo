using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.CoreBankApi;

/// <summary>
/// Tier 2 (real PostgreSQL via <see cref="CoreBankApiPostgresTestBase"/>) for
/// <see cref="InboxMessageRepository"/>: <see cref="IInboxMessageRepository.FindByIdempotencyKeyAsync"/>
/// found/not-found, and <see cref="MessageRepositoryBase{TMessage,TDbContext}.StoreIfNewAsync"/>'s
/// duplicate-race behavior for this store's unique index on <c>IdempotencyKey</c>.
/// </summary>
public class InboxMessageRepositoryTests(PostgresContainerFixture fixture) : CoreBankApiPostgresTestBase(fixture)
{
    private const string TransactionId = "txn-abc";

    [Fact]
    public async Task FindByIdempotencyKeyAsync_returns_the_matching_row_when_it_exists()
    {
        await using var context = CreateContext();
        var message = NewMessage(TransactionId);
        context.InboxMessages.Add(message);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new InboxMessageRepository(context, TimeProvider);

        var result = await repository.FindByIdempotencyKeyAsync(TransactionId, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Id.Should().Be(message.Id);
        result.TransactionId.Should().Be(TransactionId);
    }

    [Fact]
    public async Task FindByIdempotencyKeyAsync_returns_null_when_no_row_matches()
    {
        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, TimeProvider);

        var result = await repository.FindByIdempotencyKeyAsync("unknown-txn", TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task StoreIfNewAsync_stores_a_fresh_row_and_returns_true()
    {
        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, TimeProvider);
        var message = NewMessage(TransactionId);

        var result = await repository.StoreIfNewAsync(message, TestContext.Current.CancellationToken);

        result.Should().BeTrue();

        await using var verifyContext = CreateContext();
        var stored = await new InboxMessageRepository(verifyContext, TimeProvider)
            .FindByIdempotencyKeyAsync(TransactionId, TestContext.Current.CancellationToken);
        stored.Should().NotBeNull();
    }

    [Fact]
    public async Task StoreIfNewAsync_returns_false_and_does_not_throw_for_a_duplicate_idempotency_key()
    {
        await using var seedContext = CreateContext();
        seedContext.InboxMessages.Add(NewMessage(TransactionId));
        await seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, TimeProvider);
        var duplicate = NewMessage(TransactionId);

        var result = await repository.StoreIfNewAsync(duplicate, TestContext.Current.CancellationToken);

        result.Should().BeFalse();

        // The DbContext must stay usable after a lost dedupe race (inherited
        // MessageRepositoryBase behavior: the failed entity is detached).
        var count = await context.InboxMessages.CountAsync(TestContext.Current.CancellationToken);
        count.Should().Be(1);
    }

    private InboxMessage NewMessage(string transactionId) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = transactionId,
        TransactionId = transactionId,
        FromAccount = "NL91ABNA0417164300",
        ToAccount = "NL20INGB0001234567",
        Amount = 50m,
        Currency = "EUR",
        PartitionId = 0,
        Status = MessageConstants.Status.Pending,
        ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime
    };
}
