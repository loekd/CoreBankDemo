using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

public class PaymentsDbContextTests(PostgresContainerFixture fixture) : PaymentsPostgresTestBase(fixture)
{
    [Fact]
    public async Task Model_matches_payment_store_schema()
    {
        await using var store = CreateStore();
        await using var context = store.CreateContext();

        var outbox = context.Model.FindEntityType(typeof(OutboxMessage))!;
        outbox.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal(nameof(OutboxMessage.Id));
        AssertIndex(outbox.GetIndexes(), true, nameof(OutboxMessage.IdempotencyKey));
        AssertIndex(
            outbox.GetIndexes(),
            false,
            nameof(OutboxMessage.PartitionId),
            nameof(OutboxMessage.Status),
            nameof(OutboxMessage.CreatedAt));
        AssertIndex(outbox.GetIndexes(), false, nameof(OutboxMessage.Status));
        AssertIndex(outbox.GetIndexes(), false, nameof(OutboxMessage.CreatedAt));
        outbox.FindProperty(nameof(OutboxMessage.Status))!.IsConcurrencyToken.Should().BeTrue();
        outbox.FindProperty(nameof(OutboxMessage.Amount))!.GetPrecision().Should().Be(18);
        outbox.FindProperty(nameof(OutboxMessage.Amount))!.GetScale().Should().Be(2);
        AssertMaxLength(outbox, nameof(OutboxMessage.IdempotencyKey), 100);
        AssertMaxLength(outbox, nameof(OutboxMessage.TransactionId), 100);
        AssertMaxLength(outbox, nameof(OutboxMessage.FromAccount), 50);
        AssertMaxLength(outbox, nameof(OutboxMessage.ToAccount), 50);
        AssertMaxLength(outbox, nameof(OutboxMessage.Currency), 3);
        AssertMaxLength(outbox, nameof(OutboxMessage.Status), 20);
        AssertMaxLength(outbox, nameof(OutboxMessage.TraceParent), 55);
        AssertMaxLength(outbox, nameof(OutboxMessage.TraceState), 512);
        AssertRequired(
            outbox,
            nameof(OutboxMessage.IdempotencyKey),
            nameof(OutboxMessage.TransactionId),
            nameof(OutboxMessage.FromAccount),
            nameof(OutboxMessage.ToAccount),
            nameof(OutboxMessage.Currency),
            nameof(OutboxMessage.Status));

        var inbox = context.Model.FindEntityType(typeof(InboxMessage))!;
        inbox.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal(nameof(InboxMessage.Id));
        AssertIndex(
            inbox.GetIndexes(),
            true,
            nameof(InboxMessage.TransactionId),
            nameof(InboxMessage.EventType),
            nameof(InboxMessage.AccountNumber));
        AssertIndex(
            inbox.GetIndexes(),
            false,
            nameof(InboxMessage.PartitionId),
            nameof(InboxMessage.Status),
            nameof(InboxMessage.ReceivedAt));
        AssertIndex(inbox.GetIndexes(), false, nameof(InboxMessage.Status));
        AssertIndex(inbox.GetIndexes(), false, nameof(InboxMessage.ReceivedAt));
        inbox.FindProperty(nameof(InboxMessage.Status))!.IsConcurrencyToken.Should().BeTrue();
        AssertMaxLength(inbox, nameof(InboxMessage.IdempotencyKey), 100);
        AssertMaxLength(inbox, nameof(InboxMessage.TransactionId), 100);
        AssertMaxLength(inbox, nameof(InboxMessage.EventType), 100);
        AssertMaxLength(inbox, nameof(InboxMessage.AccountNumber), 50);
        AssertMaxLength(inbox, nameof(InboxMessage.Status), 20);
        AssertMaxLength(inbox, nameof(InboxMessage.TraceParent), 55);
        AssertMaxLength(inbox, nameof(InboxMessage.TraceState), 512);
        AssertRequired(
            inbox,
            nameof(InboxMessage.IdempotencyKey),
            nameof(InboxMessage.TransactionId),
            nameof(InboxMessage.EventType),
            nameof(InboxMessage.AccountNumber),
            nameof(InboxMessage.Payload),
            nameof(InboxMessage.Status));
    }

    [Fact]
    public async Task Outbox_duplicate_idempotency_key_is_rejected()
    {
        await using var store = CreateStore();
        await using var first = store.CreateContext();
        first.OutboxMessages.Add(PaymentsApiTestData.Outbox("duplicate"));
        await first.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var second = store.CreateContext();
        second.OutboxMessages.Add(PaymentsApiTestData.Outbox("duplicate"));
        var act = () => second.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Inbox_composite_identity_rejects_duplicate()
    {
        await using var store = CreateStore();
        await using var first = store.CreateContext();
        first.InboxMessages.Add(PaymentsApiTestData.Inbox());
        await first.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var duplicate = store.CreateContext();
        duplicate.InboxMessages.Add(PaymentsApiTestData.Inbox());
        var duplicateAct = () => duplicate.SaveChangesAsync(TestContext.Current.CancellationToken);
        await duplicateAct.Should().ThrowAsync<DbUpdateException>();

    }

    [Theory]
    [InlineData("transaction-2", "BalanceUpdated", "NL91ABNA0417164300")]
    [InlineData("transaction-1", "TransactionCompleted", "NL91ABNA0417164300")]
    [InlineData("transaction-1", "BalanceUpdated", "NL20INGB0001234567")]
    public async Task Inbox_composite_identity_allows_each_distinct_dimension(
        string transactionId,
        string eventType,
        string accountNumber)
    {
        await using var store = CreateStore();
        await using var first = store.CreateContext();
        first.InboxMessages.Add(PaymentsApiTestData.Inbox());
        await first.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var distinct = store.CreateContext();
        distinct.InboxMessages.Add(PaymentsApiTestData.Inbox(transactionId, eventType, accountNumber));
        var act = () => distinct.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Inbox_empty_account_sentinel_is_stored_and_deduped()
    {
        await using var store = CreateStore();
        await using var first = store.CreateContext();
        first.InboxMessages.Add(PaymentsApiTestData.Inbox(accountNumber: ""));
        await first.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var second = store.CreateContext();
        second.InboxMessages.Add(PaymentsApiTestData.Inbox(accountNumber: ""));
        var act = () => second.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    private static void AssertIndex(
        IEnumerable<Microsoft.EntityFrameworkCore.Metadata.IIndex> indexes,
        bool unique,
        params string[] properties) =>
        indexes.Should().ContainSingle(index =>
            index.IsUnique == unique &&
            index.Properties.Select(property => property.Name).SequenceEqual(properties));

    private static void AssertMaxLength(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entity,
        string property,
        int expected) =>
        entity.FindProperty(property)!.GetMaxLength().Should().Be(expected);

    private static void AssertRequired(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entity,
        params string[] properties)
    {
        foreach (var property in properties)
        {
            entity.FindProperty(property)!.IsNullable.Should().BeFalse();
        }
    }
}
