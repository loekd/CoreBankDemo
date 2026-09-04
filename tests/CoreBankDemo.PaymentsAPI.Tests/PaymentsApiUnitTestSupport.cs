using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// In-memory message shapes for the Docker-free unit tier (ADR-016 tier 1).
/// Nothing here touches a database; the same builders exist in
/// <c>CoreBankDemo.Persistence.IntegrationTests</c> for the rows that are
/// actually persisted.
/// </summary>
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
