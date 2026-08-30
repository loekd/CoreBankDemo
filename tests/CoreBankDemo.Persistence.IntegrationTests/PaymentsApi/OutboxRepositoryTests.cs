using AwesomeAssertions;
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
        var repository = new OutboxRepository(context, System.TimeProvider.System);
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
    public async Task Concurrent_independent_repositories_store_exactly_one_winner()
    {
        await using var store = CreateStore();
        var contexts = store.CreateCompetingContexts();
        await using var firstContext = contexts.First;
        await using var secondContext = contexts.Second;
        var first = new OutboxRepository(firstContext, System.TimeProvider.System);
        var second = new OutboxRepository(secondContext, System.TimeProvider.System);

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
