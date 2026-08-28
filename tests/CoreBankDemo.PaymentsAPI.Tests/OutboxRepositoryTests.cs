using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Outbox;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

public class OutboxRepositoryTests
{
    [Fact]
    public async Task StoreIfNewAsync_stores_once_and_returns_persisted_row_untracked()
    {
        await using var store = new SqlitePaymentsStore();
        await using var context = store.CreateContext();
        var repository = new OutboxRepository(context, TimeProvider.System);
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
        await using var store = new SqlitePaymentsStore();
        await using var firstContext = store.CreateContext();
        await using var secondContext = store.CreateContext();
        var first = new OutboxRepository(firstContext, TimeProvider.System);
        var second = new OutboxRepository(secondContext, TimeProvider.System);

        var results = await Task.WhenAll(
            first.StoreIfNewAsync(PaymentsApiTestData.Outbox("race-key"), TestContext.Current.CancellationToken),
            second.StoreIfNewAsync(PaymentsApiTestData.Outbox("race-key"), TestContext.Current.CancellationToken));

        results.Should().ContainSingle(stored => stored);
        await using var verification = store.CreateContext();
        verification.OutboxMessages.Count(message => message.IdempotencyKey == "race-key").Should().Be(1);
    }
}
