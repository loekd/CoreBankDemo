using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.PaymentsAPI.Tests;

internal sealed class SqlitePaymentsStore : IAsyncDisposable
{
    private readonly string _connectionString =
        $"Data Source=payments-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keeper;

    public SqlitePaymentsStore()
    {
        _keeper = new SqliteConnection(_connectionString);
        _keeper.Open();
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public PaymentsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseSqlite(_connectionString)
            .Options);

    public ValueTask DisposeAsync() => _keeper.DisposeAsync();
}

internal static class PaymentsApiTestData
{
    internal static OutboxMessage Outbox(string key = "payment-key") => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = key,
        TransactionId = key,
        FromAccount = "NL91ABNA0417164300",
        ToAccount = "NL20INGB0001234567",
        Amount = 12.34m,
        Currency = "EUR",
        PartitionId = 1,
        Status = MessageConstants.Status.Pending,
        CreatedAt = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc)
    };

    internal static InboxMessage Inbox(
        string transactionId = "transaction-1",
        string eventType = "BalanceUpdated",
        string accountNumber = "NL91ABNA0417164300") => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = transactionId,
        TransactionId = transactionId,
        EventType = eventType,
        AccountNumber = accountNumber,
        Payload = "{}",
        PartitionId = 1,
        Status = MessageConstants.Status.Pending,
        ReceivedAt = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc)
    };
}
