using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

/// <summary>
/// Per-test-method PostgreSQL database for the PaymentsAPI stores (ADR-016
/// tier 2), created inside the assembly-wide container fixture and populated
/// by the application's own <c>EnsureCreatedAsync</c> path.
/// </summary>
public abstract class PaymentsPostgresTestBase(PostgresContainerFixture fixture)
    : PostgresDatabaseTestBase(fixture)
{
    protected override async Task InitializeSchemaAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    /// <summary>A fresh <see cref="PaymentsDbContext"/> on its own connection to this test's database.</summary>
    protected PaymentsDbContext CreateContext() => CreateContext<PaymentsDbContext>();

    /// <summary>
    /// Handle onto this test's isolated database, for the tests whose helpers
    /// need to hand a context factory around. Disposal is a no-op: the database
    /// itself is owned (and dropped) by <see cref="PostgresDatabaseTestBase"/>.
    /// </summary>
    protected PaymentsStore CreateStore() => new(this);

    protected sealed class PaymentsStore(PaymentsPostgresTestBase owner) : IAsyncDisposable
    {
        public PaymentsDbContext CreateContext() => owner.CreateContext();

        /// <summary>
        /// Two contexts on genuinely independent connections, for tests that
        /// need real competing writers rather than a simulated race.
        /// </summary>
        public (PaymentsDbContext First, PaymentsDbContext Second) CreateCompetingContexts() =>
            (owner.CreateContext(), owner.CreateContext());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
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
