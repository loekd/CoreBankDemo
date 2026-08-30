using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.CoreBankApi;

public class TransactionExecutionHandlerTests(PostgresContainerFixture fixture) : CoreBankApiPostgresTestBase(fixture)
{
    private const string FromAccount = "NL91ABNA0417164300";
    private const string ToAccount = "NL20INGB0001234567";
    private const string TransactionId = "txn-123";

    [Fact]
    public async Task HandleAsync_commits_completion_payload_and_success_outbox_calls_for_success()
    {
        await using var context = CreateContext();
        var message = NewMessage();
        context.InboxMessages.Add(message);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var executor = new Mock<ITransactionExecutor>(MockBehavior.Strict);
        var enqueuer = new Mock<IOutboxEventEnqueuer>(MockBehavior.Strict);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var repository = new InboxMessageRepository(context, TimeProvider, businessMetrics);
        var response = new TransactionResponse(TransactionId, MessageConstants.Status.Completed, TimeProvider.GetUtcNow());
        var result = new TransactionExecutionResult(true, response, null, 50m, 75m);

        executor.Setup(x => x.ExecuteAsync(FromAccount, ToAccount, 50m, TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        enqueuer.Setup(x => x.EnqueueTransactionCompletedAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        enqueuer.Setup(x => x.EnqueueBalanceUpdatedAsync(message, FromAccount, -50m, 50m, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        enqueuer.Setup(x => x.EnqueueBalanceUpdatedAsync(message, ToAccount, 50m, 75m, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var handler = new TransactionExecutionHandler(executor.Object, enqueuer.Object, repository, context, TimeProvider, businessMetrics);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        var persisted = await context.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(MessageConstants.Status.Completed);
        persisted.ProcessedAt.Should().Be(TimeProvider.GetUtcNow().UtcDateTime);
        JsonSerializer.Deserialize<TransactionResponse>(persisted.ResponsePayload!).Should().Be(response);
        enqueuer.VerifyAll();
        executor.VerifyAll();

        // Story 6.5: recorded exactly once, only after the enclosing
        // transaction committed, plus the three directly-enqueued
        // corebank-outbox rows (never counted via MessageRepositoryBase's own
        // StoreIfNewAsync, since OutboxEventEnqueuer adds them straight to
        // the DbContext).
        listener.Measurements.Should().ContainSingle(m => m.InstrumentName == "corebankdemo.transaction.processed")
            .Which.Tags["outcome"].Should().Be("completed");
        listener.Measurements.Count(m =>
                m.InstrumentName == "corebankdemo.messaging.store.operations" &&
                Equals(m.Tags["messaging.store.name"], "corebank-outbox") &&
                Equals(m.Tags["outcome"], "added"))
            .Should().Be(3);
    }

    [Fact]
    public async Task HandleAsync_commits_completion_and_failure_event_only_for_business_rejection()
    {
        await using var context = CreateContext();
        var message = NewMessage();
        context.InboxMessages.Add(message);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var executor = new Mock<ITransactionExecutor>(MockBehavior.Strict);
        var enqueuer = new Mock<IOutboxEventEnqueuer>(MockBehavior.Strict);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var repository = new InboxMessageRepository(context, TimeProvider, businessMetrics);
        var response = new TransactionResponse(TransactionId, MessageConstants.Status.Failed, TimeProvider.GetUtcNow());
        var result = new TransactionExecutionResult(false, response, "Insufficient funds", null, null);

        executor.Setup(x => x.ExecuteAsync(FromAccount, ToAccount, 50m, TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        enqueuer.Setup(x => x.EnqueueTransactionFailedAsync(message, "Insufficient funds", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var handler = new TransactionExecutionHandler(executor.Object, enqueuer.Object, repository, context, TimeProvider, businessMetrics);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        var persisted = await context.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(MessageConstants.Status.Completed);
        persisted.ProcessedAt.Should().Be(TimeProvider.GetUtcNow().UtcDateTime);
        JsonSerializer.Deserialize<TransactionResponse>(persisted.ResponsePayload!).Should().Be(response);
        enqueuer.Verify(x => x.EnqueueTransactionCompletedAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        enqueuer.Verify(x => x.EnqueueBalanceUpdatedAsync(It.IsAny<InboxMessage>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
        enqueuer.VerifyAll();

        // Story 6.5: business rejection is a completed business outcome, never
        // processing/transport `failed` — and only one corebank-outbox row
        // (the failure event) is added.
        listener.Measurements.Should().ContainSingle(m => m.InstrumentName == "corebankdemo.transaction.processed")
            .Which.Tags["outcome"].Should().Be("business_rejected");
        listener.Measurements.Count(m =>
                m.InstrumentName == "corebankdemo.messaging.store.operations" &&
                Equals(m.Tags["messaging.store.name"], "corebank-outbox"))
            .Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_records_no_transaction_processed_metric_when_save_changes_throws_and_rolls_back()
    {
        await using var context = CreateContext();
        var message = NewMessage();
        context.InboxMessages.Add(message);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var executor = new Mock<ITransactionExecutor>(MockBehavior.Strict);
        var enqueuer = new Mock<IOutboxEventEnqueuer>(MockBehavior.Strict);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var repository = new InboxMessageRepository(context, TimeProvider, businessMetrics);
        var response = new TransactionResponse(TransactionId, MessageConstants.Status.Completed, TimeProvider.GetUtcNow());
        var result = new TransactionExecutionResult(true, response, null, 50m, 75m);

        executor.Setup(x => x.ExecuteAsync(FromAccount, ToAccount, 50m, TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        enqueuer.Setup(x => x.EnqueueTransactionCompletedAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        enqueuer.Setup(x => x.EnqueueBalanceUpdatedAsync(message, FromAccount, -50m, 50m, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        enqueuer.Setup(x => x.EnqueueBalanceUpdatedAsync(message, ToAccount, 50m, 75m, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var handler = new TransactionExecutionHandler(
            executor.Object, enqueuer.Object, repository, new ThrowingSaveCoreBankDbContext(context), TimeProvider, businessMetrics);

        var act = async () => await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom during save");

        listener.Measurements.Should().NotContain(m => m.InstrumentName == "corebankdemo.transaction.processed");
        listener.Measurements.Count(m =>
                m.InstrumentName == "corebankdemo.messaging.store.operations" &&
                Equals(m.Tags["messaging.store.name"], "corebank-outbox") &&
                Equals(m.Tags["outcome"], "failed"))
            .Should().Be(3);
    }

    [Fact]
    public async Task HandleAsync_rolls_back_persisted_state_when_save_changes_throws()
    {
        await using var context = CreateContext();
        var message = NewMessage();
        context.InboxMessages.Add(message);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var executor = new Mock<ITransactionExecutor>(MockBehavior.Strict);
        var enqueuer = new Mock<IOutboxEventEnqueuer>(MockBehavior.Strict);
        var repository = new InboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var response = new TransactionResponse(TransactionId, MessageConstants.Status.Completed, TimeProvider.GetUtcNow());
        var result = new TransactionExecutionResult(true, response, null, 50m, 75m);

        executor.Setup(x => x.ExecuteAsync(FromAccount, ToAccount, 50m, TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        enqueuer.Setup(x => x.EnqueueTransactionCompletedAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        enqueuer.Setup(x => x.EnqueueBalanceUpdatedAsync(message, FromAccount, -50m, 50m, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        enqueuer.Setup(x => x.EnqueueBalanceUpdatedAsync(message, ToAccount, 50m, 75m, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var handler = new TransactionExecutionHandler(executor.Object, enqueuer.Object, repository, new ThrowingSaveCoreBankDbContext(context), TimeProvider, TestBusinessMetrics.Instance);

        var act = async () => await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom during save");

        await using var verifyContext = CreateContext();
        var persisted = await verifyContext.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, TestContext.Current.CancellationToken);
        var outboxCount = await verifyContext.MessagingOutboxMessages.CountAsync(TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(MessageConstants.Status.Pending);
        persisted.ProcessedAt.Should().BeNull();
        persisted.ResponsePayload.Should().BeNull();
        outboxCount.Should().Be(0);
    }

    private InboxMessage NewMessage() => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = TransactionId,
        TransactionId = TransactionId,
        FromAccount = FromAccount,
        ToAccount = ToAccount,
        Amount = 50m,
        Currency = "EUR",
        PartitionId = 0,
        Status = MessageConstants.Status.Pending,
        ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime
    };

    private sealed class ThrowingSaveCoreBankDbContext(CoreBankDbContext inner)
        : CoreBankDbContext(new DbContextOptionsBuilder<CoreBankDbContext>()
            .UseNpgsql(inner.Database.GetDbConnection())
            .Options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom during save");
    }
}
