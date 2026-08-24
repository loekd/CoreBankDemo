using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Outbox;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.CoreBankAPI.Tests;

/// <summary>
/// <see cref="CoreBankDbContext"/> schema tests (spec-4-1's frozen Boundaries
/// section): keys, indexes, and MaxLength constraints verified both via EF
/// model metadata (exact shape) and via real SQLite constraint violations
/// (store test tier, AD-9) — proving the uniqueness constraints actually bite,
/// not just that they were declared.
/// </summary>
public class CoreBankDbContextTests : SqliteCoreBankApiTestBase
{
    [Fact]
    public void Account_has_primary_key_on_AccountNumber_and_index_on_IsActive()
    {
        using var context = CreateContext();
        var entityType = context.Model.FindEntityType(typeof(Account))!;

        entityType.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal(nameof(Account.AccountNumber));
        entityType.FindProperty(nameof(Account.AccountNumber))!.GetMaxLength().Should().Be(50);
        entityType.FindProperty(nameof(Account.AccountHolderName))!.GetMaxLength().Should().Be(200);
        entityType.FindProperty(nameof(Account.Currency))!.GetMaxLength().Should().Be(3);

        var indexes = entityType.GetIndexes().ToList();
        indexes.Should().ContainSingle(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(Account.IsActive) }) && !i.IsUnique);
    }

    [Fact]
    public void InboxMessage_has_key_indexes_and_maxlengths()
    {
        using var context = CreateContext();
        var entityType = context.Model.FindEntityType(typeof(InboxMessage))!;

        entityType.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal(nameof(InboxMessage.Id));

        var indexes = entityType.GetIndexes().ToList();
        indexes.Should().ContainSingle(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(InboxMessage.IdempotencyKey) }) && i.IsUnique);
        indexes.Should().ContainSingle(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(
                new[] { nameof(InboxMessage.PartitionId), nameof(InboxMessage.Status), nameof(InboxMessage.ReceivedAt) })
            && !i.IsUnique);
        indexes.Should().ContainSingle(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(InboxMessage.Status) }) && !i.IsUnique);
        indexes.Should().ContainSingle(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(InboxMessage.ReceivedAt) }) && !i.IsUnique);

        entityType.FindProperty(nameof(InboxMessage.IdempotencyKey))!.GetMaxLength().Should().Be(100);
        entityType.FindProperty(nameof(InboxMessage.FromAccount))!.GetMaxLength().Should().Be(50);
        entityType.FindProperty(nameof(InboxMessage.ToAccount))!.GetMaxLength().Should().Be(50);
        entityType.FindProperty(nameof(InboxMessage.Currency))!.GetMaxLength().Should().Be(3);
        entityType.FindProperty(nameof(InboxMessage.TransactionId))!.GetMaxLength().Should().Be(100);
        entityType.FindProperty(nameof(InboxMessage.Status))!.GetMaxLength().Should().Be(20);
        entityType.FindProperty(nameof(InboxMessage.TraceParent))!.GetMaxLength().Should().Be(55);
        entityType.FindProperty(nameof(InboxMessage.TraceState))!.GetMaxLength().Should().Be(512);
    }

    [Fact]
    public void MessagingOutboxMessage_has_key_indexes_and_maxlengths()
    {
        using var context = CreateContext();
        var entityType = context.Model.FindEntityType(typeof(MessagingOutboxMessage))!;

        entityType.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal(nameof(MessagingOutboxMessage.Id));

        var indexes = entityType.GetIndexes().ToList();
        indexes.Should().ContainSingle(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(
                new[] { nameof(MessagingOutboxMessage.PartitionId), nameof(MessagingOutboxMessage.Status), nameof(MessagingOutboxMessage.CreatedAt) })
            && !i.IsUnique);
        indexes.Should().ContainSingle(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(
                new[] { nameof(MessagingOutboxMessage.TransactionId), nameof(MessagingOutboxMessage.EventType), nameof(MessagingOutboxMessage.AccountNumber) })
            && i.IsUnique);
        indexes.Should().ContainSingle(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(MessagingOutboxMessage.Status) }) && !i.IsUnique);

        entityType.FindProperty(nameof(MessagingOutboxMessage.TransactionId))!.GetMaxLength().Should().Be(100);
        entityType.FindProperty(nameof(MessagingOutboxMessage.Status))!.GetMaxLength().Should().Be(20);
        entityType.FindProperty(nameof(MessagingOutboxMessage.EventType))!.GetMaxLength().Should().Be(100);
        entityType.FindProperty(nameof(MessagingOutboxMessage.EventSource))!.GetMaxLength().Should().Be(200);
        entityType.FindProperty(nameof(MessagingOutboxMessage.TraceParent))!.GetMaxLength().Should().Be(55);
        entityType.FindProperty(nameof(MessagingOutboxMessage.TraceState))!.GetMaxLength().Should().Be(512);
    }

    [Fact]
    public async Task Accounts_duplicate_AccountNumber_throws_on_save()
    {
        await using var context = CreateContext();
        context.Accounts.Add(NewAccount("NL91ABNA0417164300"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var context2 = CreateContext();
        context2.Accounts.Add(NewAccount("NL91ABNA0417164300"));
        var act = async () => await context2.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task InboxMessages_duplicate_IdempotencyKey_throws_on_save()
    {
        await using var context = CreateContext();
        context.InboxMessages.Add(NewInboxMessage("dup-key"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var context2 = CreateContext();
        context2.InboxMessages.Add(NewInboxMessage("dup-key"));
        var act = async () => await context2.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task InboxMessages_distinct_IdempotencyKey_succeeds()
    {
        await using var context = CreateContext();
        context.InboxMessages.Add(NewInboxMessage("key-1"));
        context.InboxMessages.Add(NewInboxMessage("key-2"));

        var act = async () => await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task MessagingOutboxMessages_duplicate_composite_key_throws_on_save()
    {
        await using var context = CreateContext();
        context.MessagingOutboxMessages.Add(NewOutboxMessage("txn-1", "BalanceUpdated", "NL91ABNA0417164300"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var context2 = CreateContext();
        context2.MessagingOutboxMessages.Add(NewOutboxMessage("txn-1", "BalanceUpdated", "NL91ABNA0417164300"));
        var act = async () => await context2.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task MessagingOutboxMessages_same_transaction_and_event_different_account_succeeds()
    {
        // Same (TransactionId, EventType) pair but a different AccountNumber is
        // the legitimate case of one transaction yielding two BalanceUpdated
        // events (from-account and to-account) — proves the dedupe key is the
        // full 3-column composite, not just (TransactionId, EventType).
        await using var context = CreateContext();
        context.MessagingOutboxMessages.Add(NewOutboxMessage("txn-1", "BalanceUpdated", "NL91ABNA0417164300"));
        context.MessagingOutboxMessages.Add(NewOutboxMessage("txn-1", "BalanceUpdated", "NL20INGB0001234567"));

        var act = async () => await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    private static Account NewAccount(string accountNumber) => new()
    {
        AccountNumber = accountNumber,
        AccountHolderName = "Test Holder",
        Balance = 100m,
        Currency = "EUR",
        IsActive = true,
        CreatedAt = new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc)
    };

    private static InboxMessage NewInboxMessage(string idempotencyKey) => new()
    {
        IdempotencyKey = idempotencyKey,
        FromAccount = "NL91ABNA0417164300",
        ToAccount = "NL20INGB0001234567",
        Amount = 10m,
        Currency = "EUR",
        TransactionId = idempotencyKey,
        ReceivedAt = new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc)
    };

    private static MessagingOutboxMessage NewOutboxMessage(string transactionId, string eventType, string accountNumber) => new()
    {
        IdempotencyKey = transactionId,
        TransactionId = transactionId,
        Status = Messaging.MessageConstants.Status.Pending,
        EventType = eventType,
        EventSource = "CoreBankAPI",
        AccountNumber = accountNumber,
        ToAccount = "NL20INGB0001234567",
        Amount = 10m,
        Currency = "EUR",
        TransactionStatus = "Completed",
        CreatedAt = new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc)
    };
}
