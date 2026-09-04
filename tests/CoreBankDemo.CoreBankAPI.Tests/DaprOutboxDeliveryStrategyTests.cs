using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using Moq;
using Xunit;

namespace CoreBankDemo.CoreBankAPI.Tests;

public class DaprOutboxDeliveryStrategyTests
{
    private static readonly DateTime OccurredAt =
        new(2026, 8, 28, 12, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Completed_row_publishes_the_exact_stored_metadata_and_payload()
    {
        var publisher = new Mock<IEventPublisher>();
        var message = NewMessage(Constants.TransactionCompleted);
        var strategy = new DaprOutboxDeliveryStrategy(publisher.Object);

        await strategy.DeliverAsync(message, TestContext.Current.CancellationToken);

        publisher.Verify(p => p.PublishAsync(
            message.EventType,
            message.EventSource,
            message.TransactionId,
            It.Is<TransactionCompletedEvent>(payload =>
                payload.TransactionId == message.TransactionId
                && payload.Status == message.TransactionStatus
                && payload.ProcessedAt == new DateTimeOffset(OccurredAt)),
            message.TraceParent,
            message.TraceState,
            TestContext.Current.CancellationToken), Times.Once);
    }

    [Fact]
    public async Task Completed_row_converts_a_local_occurrence_time_to_utc()
    {
        var publisher = new Mock<IEventPublisher>();
        var message = NewMessage(Constants.TransactionCompleted);
        message.EventOccurredAt = DateTime.SpecifyKind(OccurredAt, DateTimeKind.Local);
        var strategy = new DaprOutboxDeliveryStrategy(publisher.Object);

        await strategy.DeliverAsync(message, TestContext.Current.CancellationToken);

        publisher.Verify(p => p.PublishAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.Is<TransactionCompletedEvent>(payload =>
                payload.ProcessedAt == new DateTimeOffset(message.EventOccurredAt.ToUniversalTime())),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            TestContext.Current.CancellationToken), Times.Once);
    }

    [Fact]
    public async Task Failed_row_publishes_the_stored_occurrence_time_and_nullable_reason()
    {
        var publisher = new Mock<IEventPublisher>();
        var message = NewMessage(Constants.TransactionFailed);
        message.TransactionStatus = MessageConstants.Status.Failed;
        message.ErrorReason = null;
        var strategy = new DaprOutboxDeliveryStrategy(publisher.Object);

        await strategy.DeliverAsync(message, TestContext.Current.CancellationToken);

        publisher.Verify(p => p.PublishAsync(
            message.EventType,
            message.EventSource,
            message.TransactionId,
            It.Is<TransactionFailedEvent>(payload =>
                payload.TransactionId == message.TransactionId
                && payload.Status == MessageConstants.Status.Failed
                && payload.ProcessedAt == new DateTimeOffset(OccurredAt)
                && payload.ErrorReason == null),
            message.TraceParent,
            message.TraceState,
            TestContext.Current.CancellationToken), Times.Once);
    }

    [Fact]
    public async Task Balance_row_publishes_transaction_account_delta_balance_and_currency()
    {
        var publisher = new Mock<IEventPublisher>();
        var message = NewMessage(Constants.BalanceUpdated);
        message.Amount = -12.50m;
        message.NewBalance = 87.50m;
        var strategy = new DaprOutboxDeliveryStrategy(publisher.Object);

        await strategy.DeliverAsync(message, TestContext.Current.CancellationToken);

        publisher.Verify(p => p.PublishAsync(
            message.EventType,
            message.EventSource,
            message.TransactionId,
            It.Is<BalanceUpdatedEvent>(payload =>
                payload.TransactionId == message.TransactionId
                && payload.AccountNumber == message.AccountNumber
                && payload.Delta == -12.50m
                && payload.NewBalance == 87.50m
                && payload.Currency == message.Currency),
            message.TraceParent,
            message.TraceState,
            TestContext.Current.CancellationToken), Times.Once);
    }

    [Fact]
    public async Task Balance_row_without_new_balance_throws_without_publishing()
    {
        var publisher = new Mock<IEventPublisher>();
        var message = NewMessage(Constants.BalanceUpdated);
        message.NewBalance = null;
        var strategy = new DaprOutboxDeliveryStrategy(publisher.Object);

        var act = async () => await strategy.DeliverAsync(message);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing NewBalance*");
        publisher.Verify(
            p => p.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Unsupported_event_type_throws_without_publishing()
    {
        var publisher = new Mock<IEventPublisher>();
        var message = NewMessage("com.corebank.unknown");
        var strategy = new DaprOutboxDeliveryStrategy(publisher.Object);

        var act = async () => await strategy.DeliverAsync(message);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*com.corebank.unknown*");
        publisher.Verify(
            p => p.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Transport_exception_propagates_and_retry_payload_keeps_original_occurrence_time()
    {
        var publisher = new Mock<IEventPublisher>();
        var publishedTimes = new List<DateTimeOffset>();
        publisher.Setup(p => p.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, object, string?, string?, CancellationToken>(
                (_, _, _, payload, _, _, _) =>
                    publishedTimes.Add(((TransactionCompletedEvent)payload).ProcessedAt))
            .ThrowsAsync(new InvalidOperationException("transport unavailable"));
        var message = NewMessage(Constants.TransactionCompleted);
        var strategy = new DaprOutboxDeliveryStrategy(publisher.Object);

        var first = async () => await strategy.DeliverAsync(message);
        await first.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("transport unavailable");

        message.CreatedAt = message.CreatedAt.AddHours(2);
        var retry = async () => await strategy.DeliverAsync(message);
        await retry.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("transport unavailable");

        publishedTimes.Should().Equal(
            new DateTimeOffset(OccurredAt),
            new DateTimeOffset(OccurredAt));
    }

    private static MessagingOutboxMessage NewMessage(string eventType) => new()
    {
        Id = Guid.NewGuid(),
        PartitionId = 0,
        IdempotencyKey = "txn-123",
        Status = MessageConstants.Status.Processing,
        CreatedAt = OccurredAt.AddMinutes(1),
        EventOccurredAt = OccurredAt,
        TransactionId = "txn-123",
        EventType = eventType,
        EventSource = "https://corebank-api/transactions",
        AccountNumber = "NL91ABNA0417164300",
        ToAccount = "NL20INGB0001234567",
        Amount = 12.50m,
        NewBalance = 87.50m,
        Currency = "EUR",
        TransactionStatus = MessageConstants.Status.Completed,
        TraceParent = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01",
        TraceState = "vendor=value"
    };
}
