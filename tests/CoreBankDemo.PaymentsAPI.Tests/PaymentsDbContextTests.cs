using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

public sealed class PaymentsDbContextTests
{
    [Fact]
    public void Outbox_schema_matches_the_payment_store_contract()
    {
        using var connection = OpenConnection();
        using var context = CreateContext(connection);
        var entity = context.Model.FindEntityType(typeof(OutboxMessage))!;

        entity.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal(nameof(OutboxMessage.Id));
        entity.GetIndexes().Should().ContainSingle(index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(OutboxMessage.IdempotencyKey) }));
        entity.GetIndexes().Should().ContainSingle(index =>
            !index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(OutboxMessage.PartitionId), nameof(OutboxMessage.Status), nameof(OutboxMessage.CreatedAt) }));
        entity.GetIndexes().Should().ContainSingle(index =>
            !index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(OutboxMessage.Status) }));
        entity.GetIndexes().Should().ContainSingle(index =>
            !index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(OutboxMessage.CreatedAt) }));

        AssertProperty(entity, nameof(OutboxMessage.IdempotencyKey), 100, false);
        AssertProperty(entity, nameof(OutboxMessage.TransactionId), 100, false);
        AssertProperty(entity, nameof(OutboxMessage.FromAccount), 50, false);
        AssertProperty(entity, nameof(OutboxMessage.ToAccount), 50, false);
        AssertProperty(entity, nameof(OutboxMessage.Currency), 3, false);
        AssertProperty(entity, nameof(OutboxMessage.Status), 20, false);
        AssertProperty(entity, nameof(OutboxMessage.TraceParent), 55, true);
        AssertProperty(entity, nameof(OutboxMessage.TraceState), 512, true);
        entity.FindProperty(nameof(OutboxMessage.Status))!.IsConcurrencyToken.Should().BeTrue();
    }

    [Fact]
    public void Inbox_schema_matches_the_event_dedupe_contract()
    {
        using var connection = OpenConnection();
        using var context = CreateContext(connection);
        var entity = context.Model.FindEntityType(typeof(InboxMessage))!;

        entity.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal(nameof(InboxMessage.Id));
        entity.GetIndexes().Should().ContainSingle(index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(InboxMessage.TransactionId), nameof(InboxMessage.EventType), nameof(InboxMessage.AccountNumber) }));
        entity.GetIndexes().Should().ContainSingle(index =>
            !index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(InboxMessage.PartitionId), nameof(InboxMessage.Status), nameof(InboxMessage.ReceivedAt) }));
        entity.GetIndexes().Should().ContainSingle(index =>
            !index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(InboxMessage.Status) }));
        entity.GetIndexes().Should().ContainSingle(index =>
            !index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(InboxMessage.ReceivedAt) }));

        AssertProperty(entity, nameof(InboxMessage.IdempotencyKey), 100, false);
        AssertProperty(entity, nameof(InboxMessage.TransactionId), 100, false);
        AssertProperty(entity, nameof(InboxMessage.EventType), 100, false);
        AssertProperty(entity, nameof(InboxMessage.AccountNumber), 50, false);
        AssertProperty(entity, nameof(InboxMessage.EventPayload), null, false);
        AssertProperty(entity, nameof(InboxMessage.Status), 20, false);
        AssertProperty(entity, nameof(InboxMessage.TraceParent), 55, true);
        AssertProperty(entity, nameof(InboxMessage.TraceState), 512, true);
        entity.FindProperty(nameof(InboxMessage.Status))!.IsConcurrencyToken.Should().BeTrue();
    }

    [Fact]
    public async Task Outbox_rejects_a_duplicate_idempotency_key()
    {
        await using var connection = OpenConnection();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        context.OutboxMessages.Add(NewOutbox("same-key"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();
        context.OutboxMessages.Add(NewOutbox("same-key"));

        var act = async () => await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Inbox_rejects_the_same_event_identity_but_accepts_a_different_account()
    {
        await using var connection = OpenConnection();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        context.InboxMessages.Add(NewInbox("txn", "BalanceUpdated", "account-1"));
        context.InboxMessages.Add(NewInbox("txn", "BalanceUpdated", "account-2"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();
        context.InboxMessages.Add(NewInbox("txn", "BalanceUpdated", "account-1"));

        var act = async () => await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
        (await context.InboxMessages.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(2);
    }

    private static void AssertProperty(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entity,
        string name,
        int? maxLength,
        bool nullable)
    {
        var property = entity.FindProperty(name)!;
        property.GetMaxLength().Should().Be(maxLength);
        property.IsNullable.Should().Be(nullable);
    }

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static PaymentsDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlite(connection).Options);

    private static OutboxMessage NewOutbox(string key) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = key,
        TransactionId = key,
        FromAccount = "from",
        ToAccount = "to",
        Amount = 10m,
        Currency = "EUR",
        CreatedAt = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc)
    };

    private static InboxMessage NewInbox(string transactionId, string eventType, string accountNumber) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = transactionId,
        TransactionId = transactionId,
        EventType = eventType,
        AccountNumber = accountNumber,
        EventPayload = "{}",
        ReceivedAt = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc),
        Status = MessageConstants.Status.Pending
    };
}
