using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.PaymentsAPI.Accounts;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

/// <summary>
/// The transactional inbox on real PostgreSQL (spec: payments-account-projection):
/// the projection write and the inbox row's completion are one commit.
/// </summary>
public class TransactionEventHandlerTests(PostgresContainerFixture fixture) : PaymentsPostgresTestBase(fixture)
{
    private const string Debtor = "NL91ABNA0417164300";

    [Fact]
    public async Task HandleAsync_commits_the_debtor_settlement_the_release_and_the_completion_together()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var seed = CreateContext())
        {
            seed.OutboxMessages.Add(PaymentsApiTestData.Outbox("txn-s")); // Debtor, 12.34
            seed.ProjectedAccounts.Add(new ProjectedAccount
            {
                AccountNumber = Debtor, SettledBalance = 100m, Reserved = 12.34m, Currency = "EUR",
                UpdatedAt = TimeProvider.GetUtcNow().UtcDateTime
            });
            seed.InboxMessages.Add(BalanceUpdated("txn-s", -12.34m, 87.66m)); // claimed: Processing, as the kernel hands it over
            await seed.SaveChangesAsync(ct);
        }
        await using var context = CreateContext();
        var message = await context.InboxMessages.AsNoTracking().SingleAsync(ct);
        var handler = CreateHandler(context);

        await handler.HandleAsync(message, ct);

        await using var verification = CreateContext();
        var account = await verification.ProjectedAccounts.SingleAsync(ct);
        account.SettledBalance.Should().Be(87.66m);
        account.Reserved.Should().Be(0m);
        var row = await verification.InboxMessages.SingleAsync(ct);
        row.Status.Should().Be(MessageConstants.Status.Completed);
        row.ProcessedAt.Should().Be(TimeProvider.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task HandleAsync_rolls_back_projection_when_completion_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var seed = CreateContext())
        {
            seed.InboxMessages.Add(BalanceUpdated("txn-f", -1m, 99m));
            await seed.SaveChangesAsync(ct);
        }
        await using var context = CreateContext();
        var message = await context.InboxMessages.AsNoTracking().SingleAsync(ct);
        // Every dependency runs on the failing context, so the transaction is
        // opened on it and the projection's INSERT … ON CONFLICT executes inside
        // that transaction. The first SaveChangesAsync (the settlement) succeeds;
        // the second (the inbox row's completion) throws, so the projection
        // write is already in the transaction when it rolls back.
        await using var failing = new ThrowingSavePaymentsDbContext(context, failingSave: 2);
        var handler = new TransactionEventHandler(
            NullLogger<TransactionEventHandler>.Instance,
            new OutboxRepository(failing, TimeProvider, TestBusinessMetrics.Instance),
            new InboxMessageRepository(failing, TimeProvider, TestBusinessMetrics.Instance),
            new AccountProjectionStore(failing, TimeProvider));

        var act = () => handler.HandleAsync(message, ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom during save");
        await using var verification = CreateContext();
        (await verification.ProjectedAccounts.CountAsync(ct)).Should().Be(0, "the upsert ran inside the rolled-back transaction");
        (await verification.InboxMessages.SingleAsync(ct)).Status.Should().Be(MessageConstants.Status.Processing, "left as claimed for the kernel to retry");
    }

    private TransactionEventHandler CreateHandler(PaymentsDbContext context) => new(
        NullLogger<TransactionEventHandler>.Instance,
        new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance),
        new InboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance),
        new AccountProjectionStore(context, TimeProvider));

    private InboxMessage BalanceUpdated(string transactionId, decimal delta, decimal newBalance) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = transactionId,
        TransactionId = transactionId,
        EventType = Constants.BalanceUpdated,
        AccountNumber = Debtor,
        Payload = JsonSerializer.Serialize(new BalanceUpdatedEvent(transactionId, Debtor, delta, newBalance, "EUR")),
        PartitionId = 0,
        Status = MessageConstants.Status.Processing,
        ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime
    };

    /// <summary>Shares the inner context's connection so it joins the same database; the <paramref name="failingSave"/>-th save throws.</summary>
    private sealed class ThrowingSavePaymentsDbContext(PaymentsDbContext inner, int failingSave)
        : PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(inner.Database.GetDbConnection())
            .Options)
    {
        private int _saves;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            ++_saves == failingSave
                ? throw new InvalidOperationException("boom during save")
                : base.SaveChangesAsync(cancellationToken);
    }
}
