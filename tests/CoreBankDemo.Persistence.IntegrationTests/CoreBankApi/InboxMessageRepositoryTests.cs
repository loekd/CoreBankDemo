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

        var repository = new InboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var result = await repository.FindByIdempotencyKeyAsync(TransactionId, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Id.Should().Be(message.Id);
        result.TransactionId.Should().Be(TransactionId);
    }

    [Fact]
    public async Task FindByIdempotencyKeyAsync_returns_null_when_no_row_matches()
    {
        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var result = await repository.FindByIdempotencyKeyAsync("unknown-txn", TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task StoreIfNewAsync_stores_a_fresh_row_and_returns_true()
    {
        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var message = NewMessage(TransactionId);

        var result = await repository.StoreIfNewAsync(message, TestContext.Current.CancellationToken);

        result.Should().BeTrue();

        await using var verifyContext = CreateContext();
        var stored = await new InboxMessageRepository(verifyContext, TimeProvider, TestBusinessMetrics.Instance)
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
        var repository = new InboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var duplicate = NewMessage(TransactionId);

        var result = await repository.StoreIfNewAsync(duplicate, TestContext.Current.CancellationToken);

        result.Should().BeFalse();

        // The DbContext must stay usable after a lost dedupe race (inherited
        // MessageRepositoryBase behavior: the failed entity is detached).
        var count = await context.InboxMessages.CountAsync(TestContext.Current.CancellationToken);
        count.Should().Be(1);
    }

    // ---- Spec: add-instant-payment-rail, review loop 1 -- InboxMessage.Status
    // as an EF concurrency token, and TryClaimByIdAsync (inherited from
    // MessageRepositoryBase) for CoreBankAPI's InboxMessage. Mirrors
    // CoreBankDemo.Persistence.IntegrationTests.Messaging.TryClaimByIdAsyncTests
    // (the Payments-side proof) exactly, against the real InboxMessage/
    // CoreBankDbContext this time. ----

    [Fact]
    public async Task TryClaimByIdAsync_claims_a_pending_row_and_transitions_it_to_processing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seedContext = CreateContext();
        var message = NewMessage(TransactionId);
        seedContext.InboxMessages.Add(message);
        await seedContext.SaveChangesAsync(ct);

        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var claimed = await repository.TryClaimByIdAsync(message.Id, ct);

        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(message.Id);
        claimed.Status.Should().Be(MessageConstants.Status.Processing);
    }

    [Theory]
    [InlineData(MessageConstants.Status.Processing)]
    [InlineData(MessageConstants.Status.Completed)]
    [InlineData(MessageConstants.Status.Failed)]
    public async Task TryClaimByIdAsync_returns_null_for_a_row_that_is_not_pending(string status)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seedContext = CreateContext();
        var message = NewMessage(TransactionId);
        message.Status = status;
        seedContext.InboxMessages.Add(message);
        await seedContext.SaveChangesAsync(ct);

        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var claimed = await repository.TryClaimByIdAsync(message.Id, ct);

        claimed.Should().BeNull();
    }

    [Fact]
    public async Task A_row_claimed_inline_is_no_longer_claimable_by_a_background_batch_claim()
    {
        // Reproduces the exact race the review confirmed exploitable: the
        // inline path (TransactionIntakeHandler.TryExecuteInlineAsync) claims
        // a just-stored row via TryClaimByIdAsync at the same moment the
        // background InboxProcessorBase's own batch claim could reach it.
        // Exactly one caller may ever win -- the other must see it as
        // unclaimable, never both executing the same ledger mutation.
        var ct = TestContext.Current.CancellationToken;
        await using var seedContext = CreateContext();
        var message = NewMessage(TransactionId);
        seedContext.InboxMessages.Add(message);
        await seedContext.SaveChangesAsync(ct);

        await using var inlineContext = CreateContext();
        await using var backgroundContext = CreateContext();
        var inlineRepository = new InboxMessageRepository(inlineContext, TimeProvider, TestBusinessMetrics.Instance);
        var backgroundRepository = new InboxMessageRepository(backgroundContext, TimeProvider, TestBusinessMetrics.Instance);

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
        var message = NewMessage(TransactionId);
        seedContext.InboxMessages.Add(message);
        await seedContext.SaveChangesAsync(ct);

        await using var inlineContext = CreateContext();
        await using var backgroundContext = CreateContext();
        var inlineRepository = new InboxMessageRepository(inlineContext, TimeProvider, TestBusinessMetrics.Instance);
        var backgroundRepository = new InboxMessageRepository(backgroundContext, TimeProvider, TestBusinessMetrics.Instance);

        var backgroundClaim = await backgroundRepository.ClaimBatchForPartitionAsync(partitionId: 0, batchSize: 10, ct);
        var inlineClaim = await inlineRepository.TryClaimByIdAsync(message.Id, ct);

        backgroundClaim.Should().ContainSingle(m => m.Id == message.Id);
        inlineClaim.Should().BeNull();
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
