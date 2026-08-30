using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.PaymentsAPI.Inbox;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

/// <summary>
/// Exercises <see cref="InboxMessageRepository"/>'s race-safe, insert-first
/// dedupe (spec-5-5): a fresh <c>(TransactionId, EventType, AccountNumber)</c>
/// identity stores once; the exact same identity redelivered -- concurrently
/// or sequentially -- loses the unique-index race and reports "already
/// exists" without a second row or an exception (AD-4: never
/// check-then-insert), while a distinct identity for the same transaction
/// (another event type or account) stores independently.
/// </summary>
public class InboxMessageRepositoryTests(PostgresContainerFixture fixture) : PaymentsPostgresTestBase(fixture)
{
    [Fact]
    public async Task StoreIfNewAsync_stores_once_and_reports_duplicates_as_already_exists()
    {
        await using var store = CreateStore();
        await using var context = store.CreateContext();
        var repository = new InboxMessageRepository(context, System.TimeProvider.System);

        (await repository.StoreIfNewAsync(
            PaymentsApiTestData.Inbox("transaction-1", "com.corebank.transaction.completed", ""),
            TestContext.Current.CancellationToken)).Should().BeTrue();
        (await repository.StoreIfNewAsync(
            PaymentsApiTestData.Inbox("transaction-1", "com.corebank.transaction.completed", ""),
            TestContext.Current.CancellationToken)).Should().BeFalse();

        context.InboxMessages.Count(m =>
                m.TransactionId == "transaction-1" &&
                m.EventType == "com.corebank.transaction.completed" &&
                m.AccountNumber == "")
            .Should().Be(1);
    }

    [Fact]
    public async Task StoreIfNewAsync_stores_distinct_identities_for_the_same_transaction_independently()
    {
        await using var store = CreateStore();
        await using var context = store.CreateContext();
        var repository = new InboxMessageRepository(context, System.TimeProvider.System);

        (await repository.StoreIfNewAsync(
            PaymentsApiTestData.Inbox("transaction-2", "com.corebank.transaction.completed", ""),
            TestContext.Current.CancellationToken)).Should().BeTrue();
        (await repository.StoreIfNewAsync(
            PaymentsApiTestData.Inbox("transaction-2", "com.corebank.account.balance.updated", "NL91ABNA0417164300"),
            TestContext.Current.CancellationToken)).Should().BeTrue();
        (await repository.StoreIfNewAsync(
            PaymentsApiTestData.Inbox("transaction-2", "com.corebank.account.balance.updated", "NL20INGB0001234567"),
            TestContext.Current.CancellationToken)).Should().BeTrue();

        context.InboxMessages.Count(m => m.TransactionId == "transaction-2").Should().Be(3);
    }

    [Fact]
    public async Task Concurrent_independent_repositories_store_exactly_one_winner_for_the_same_identity()
    {
        await using var store = CreateStore();
        var contexts = store.CreateCompetingContexts();
        await using var firstContext = contexts.First;
        await using var secondContext = contexts.Second;
        var first = new InboxMessageRepository(firstContext, System.TimeProvider.System);
        var second = new InboxMessageRepository(secondContext, System.TimeProvider.System);

        var results = await Task.WhenAll(
            first.StoreIfNewAsync(
                PaymentsApiTestData.Inbox("race-transaction", "com.corebank.transaction.completed", ""),
                TestContext.Current.CancellationToken),
            second.StoreIfNewAsync(
                PaymentsApiTestData.Inbox("race-transaction", "com.corebank.transaction.completed", ""),
                TestContext.Current.CancellationToken));

        results.Should().ContainSingle(stored => stored);
        await using var verification = store.CreateContext();
        verification.InboxMessages.Count(m => m.TransactionId == "race-transaction").Should().Be(1);
    }
}
