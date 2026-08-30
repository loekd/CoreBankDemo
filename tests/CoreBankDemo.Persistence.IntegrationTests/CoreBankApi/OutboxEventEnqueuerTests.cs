using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.CoreBankApi;

public class OutboxEventEnqueuerTests(PostgresContainerFixture fixture) : CoreBankApiPostgresTestBase(fixture)
{
    [Fact]
    public async Task EnqueueTransactionCompletedAsync_builds_the_legacy_transaction_completed_row_shape()
    {
        await using var context = CreateContext();
        var enqueuer = new OutboxEventEnqueuer(context, Options.Create(new MessagingOutboxProcessingOptions { PartitionCount = 4, LockExpirySeconds = 30, PollingIntervalMs = 5000 }), TimeProvider);
        var message = NewMessage();

        await enqueuer.EnqueueTransactionCompletedAsync(message, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var row = await context.MessagingOutboxMessages.SingleAsync(TestContext.Current.CancellationToken);
        row.PartitionId.Should().Be(PartitionHelper.GetPartitionId(message.TransactionId, 4));
        row.IdempotencyKey.Should().Be(message.TransactionId);
        row.TransactionId.Should().Be(message.TransactionId);
        row.Status.Should().Be(MessageConstants.Status.Pending);
        row.EventType.Should().Be(Constants.TransactionCompleted);
        row.EventSource.Should().Be("https://corebank-api/transactions");
        row.AccountNumber.Should().Be(message.FromAccount);
        row.ToAccount.Should().Be(message.ToAccount);
        row.Amount.Should().Be(message.Amount);
        row.Currency.Should().Be(message.Currency);
        row.TransactionStatus.Should().Be(MessageConstants.Status.Completed);
        row.ErrorReason.Should().BeNull();
        row.CreatedAt.Should().Be(TimeProvider.GetUtcNow().UtcDateTime);
        row.EventOccurredAt.Should().Be(message.ProcessedAt);
        row.TraceParent.Should().Be(message.TraceParent);
        row.TraceState.Should().Be(message.TraceState);
    }

    [Fact]
    public async Task EnqueueTransactionFailedAsync_builds_the_legacy_transaction_failed_row_shape()
    {
        await using var context = CreateContext();
        var enqueuer = new OutboxEventEnqueuer(context, Options.Create(new MessagingOutboxProcessingOptions { PartitionCount = 4, LockExpirySeconds = 30, PollingIntervalMs = 5000 }), TimeProvider);
        var message = NewMessage();

        await enqueuer.EnqueueTransactionFailedAsync(message, "boom", TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var row = await context.MessagingOutboxMessages.SingleAsync(TestContext.Current.CancellationToken);
        row.EventType.Should().Be(Constants.TransactionFailed);
        row.TransactionStatus.Should().Be(MessageConstants.Status.Failed);
        row.ErrorReason.Should().Be("boom");
        row.EventOccurredAt.Should().Be(message.ProcessedAt);
        row.AccountNumber.Should().Be(message.FromAccount);
        row.TraceParent.Should().Be(message.TraceParent);
        row.TraceState.Should().Be(message.TraceState);
    }

    [Fact]
    public async Task EnqueueBalanceUpdatedAsync_builds_the_balance_row_shape_and_allows_two_rows_for_one_transaction()
    {
        await using var context = CreateContext();
        var enqueuer = new OutboxEventEnqueuer(context, Options.Create(new MessagingOutboxProcessingOptions { PartitionCount = 4, LockExpirySeconds = 30, PollingIntervalMs = 5000 }), TimeProvider);
        var message = NewMessage();

        await enqueuer.EnqueueBalanceUpdatedAsync(message, message.FromAccount, -message.Amount, 50m, TestContext.Current.CancellationToken);
        await enqueuer.EnqueueBalanceUpdatedAsync(message, message.ToAccount, message.Amount, 75m, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var rows = await context.MessagingOutboxMessages.OrderBy(x => x.AccountNumber).ToListAsync(TestContext.Current.CancellationToken);
        rows.Should().HaveCount(2);
        rows[0].EventType.Should().Be(Constants.BalanceUpdated);
        rows[0].EventSource.Should().Be("https://corebank-api/accounts");
        rows[0].IdempotencyKey.Should().Be(message.TransactionId);
        rows[0].ToAccount.Should().Be(rows[0].AccountNumber);
        rows[0].TransactionStatus.Should().Be(MessageConstants.Status.Completed);
        rows[0].PartitionId.Should().Be(PartitionHelper.GetPartitionId(rows[0].AccountNumber, 4));
        rows[1].PartitionId.Should().Be(PartitionHelper.GetPartitionId(rows[1].AccountNumber, 4));
        rows.Select(r => r.Amount).Should().BeEquivalentTo(new[] { -50m, 50m });
        rows.Select(r => r.NewBalance).Should().BeEquivalentTo(new decimal?[] { 50m, 75m });
        rows.Should().AllSatisfy(r =>
        {
            r.EventOccurredAt.Should().Be(message.ProcessedAt);
            r.TraceParent.Should().Be(message.TraceParent);
            r.TraceState.Should().Be(message.TraceState);
        });
    }

    [Fact]
    public async Task EnqueueAsync_without_a_stamped_inbox_processed_time_fails_instead_of_inventing_one()
    {
        await using var context = CreateContext();
        var enqueuer = new OutboxEventEnqueuer(
            context,
            Options.Create(new MessagingOutboxProcessingOptions
            {
                PartitionCount = 4,
                LockExpirySeconds = 30,
                PollingIntervalMs = 5000
            }),
            TimeProvider);
        var message = NewMessage();
        message.ProcessedAt = null;

        var act = async () => await enqueuer.EnqueueTransactionCompletedAsync(
            message,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must have ProcessedAt stamped*");
        context.ChangeTracker.Entries<MessagingOutboxMessage>().Should().BeEmpty();
    }

    private InboxMessage NewMessage() => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = "txn-123",
        TransactionId = "txn-123",
        FromAccount = "NL91ABNA0417164300",
        ToAccount = "NL20INGB0001234567",
        Amount = 50m,
        Currency = "EUR",
        PartitionId = 0,
        Status = MessageConstants.Status.Pending,
        ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime,
        ProcessedAt = TimeProvider.GetUtcNow().UtcDateTime,
        TraceParent = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01",
        TraceState = "congo=t61rcWkgMzE"
    };
}
