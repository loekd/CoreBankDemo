using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.PaymentsAPI.Outbox;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

public class OutboxRepositoryTests(PostgresContainerFixture fixture) : PaymentsPostgresTestBase(fixture)
{
    [Fact]
    public async Task StoreIfNewAsync_stores_once_and_returns_persisted_row_untracked()
    {
        await using var store = CreateStore();
        await using var context = store.CreateContext();
        var repository = new OutboxRepository(context, System.TimeProvider.System, TestBusinessMetrics.Instance);
        var message = PaymentsApiTestData.Outbox("repository-key");

        (await repository.StoreIfNewAsync(message, TestContext.Current.CancellationToken)).Should().BeTrue();
        (await repository.StoreIfNewAsync(
            PaymentsApiTestData.Outbox("repository-key"),
            TestContext.Current.CancellationToken)).Should().BeFalse();

        var found = await repository.FindByIdempotencyKeyAsync(
            "repository-key",
            TestContext.Current.CancellationToken);
        found.Should().NotBeNull();
        context.Entry(found!).State.Should().Be(Microsoft.EntityFrameworkCore.EntityState.Detached);
    }

    [Fact]
    public async Task RecordCommittedOutcomeAsync_upgrades_a_non_committed_cached_outcome_only_once()
    {
        // The instant rail caches whatever CoreBank answered, and a deferred
        // inline attempt answers Pending; the transaction event is the only
        // place the payment can learn that it went on to settle.
        await using var store = CreateStore();
        await using var context = store.CreateContext();
        var repository = new OutboxRepository(context, System.TimeProvider.System, TestBusinessMetrics.Instance);
        var message = PaymentsApiTestData.Outbox("deferred-key");
        message.Status = MessageConstants.Status.Completed;
        message.ResponsePayload = """{"TransactionId":"deferred-key","Status":"Pending","ProcessedAt":"2026-08-28T12:00:00+00:00"}""";
        (await repository.StoreIfNewAsync(message, TestContext.Current.CancellationToken)).Should().BeTrue();
        var settledAt = new DateTimeOffset(2026, 8, 28, 12, 0, 5, TimeSpan.Zero);

        var first = await repository.RecordCommittedOutcomeAsync(
            "deferred-key", MessageConstants.Status.Completed, settledAt, TestContext.Current.CancellationToken);
        var second = await repository.RecordCommittedOutcomeAsync(
            "deferred-key", MessageConstants.Status.Failed, settledAt, TestContext.Current.CancellationToken);

        first.Should().BeTrue();
        second.Should().BeFalse("a committed outcome is never overwritten");
        await using var verification = store.CreateContext();
        var row = verification.OutboxMessages.Single(row => row.TransactionId == "deferred-key");
        row.Status.Should().Be(MessageConstants.Status.Completed, "transport state is never touched");
        row.ResponsePayload.Should().Contain("\"Status\":\"Completed\"").And.Contain("2026-08-28T12:00:05");
    }

    [Fact]
    public async Task RecordCommittedOutcomeAsync_ignores_unknown_transactions()
    {
        await using var store = CreateStore();
        await using var context = store.CreateContext();
        var repository = new OutboxRepository(context, System.TimeProvider.System, TestBusinessMetrics.Instance);

        var recorded = await repository.RecordCommittedOutcomeAsync(
            "never-stored", MessageConstants.Status.Completed, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        recorded.Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_independent_repositories_store_exactly_one_winner()
    {
        await using var store = CreateStore();
        var contexts = store.CreateCompetingContexts();
        await using var firstContext = contexts.First;
        await using var secondContext = contexts.Second;
        var first = new OutboxRepository(firstContext, System.TimeProvider.System, TestBusinessMetrics.Instance);
        var second = new OutboxRepository(secondContext, System.TimeProvider.System, TestBusinessMetrics.Instance);

        var results = await PaymentsApiTestData.RaceAsync(
            () => first.StoreIfNewAsync(
                PaymentsApiTestData.Outbox("race-key"),
                TestContext.Current.CancellationToken),
            () => second.StoreIfNewAsync(
                PaymentsApiTestData.Outbox("race-key"),
                TestContext.Current.CancellationToken));

        results.Should().ContainSingle(stored => stored);
        await using var verification = store.CreateContext();
        verification.OutboxMessages.Count(message => message.IdempotencyKey == "race-key").Should().Be(1);
    }
}
