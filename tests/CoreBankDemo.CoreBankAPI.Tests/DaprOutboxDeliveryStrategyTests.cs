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
    private readonly Mock<IEventPublisher> _publisher = new();

    [Fact]
    public async Task DeliverAsync_maps_completed_event_and_forwards_metadata_and_cancellation()
    {
        var processedAt = new DateTime(2026, 8, 28, 12, 30, 0, DateTimeKind.Utc);
        var message = NewMessage(Constants.TransactionCompleted);
        message.ProcessedAt = processedAt;
        var cancellationToken = TestContext.Current.CancellationToken;
        var strategy = new DaprOutboxDeliveryStrategy(_publisher.Object);

        await strategy.DeliverAsync(message, cancellationToken);

        VerifyPublished(
            message,
            new TransactionCompletedEvent(message.TransactionId, message.TransactionStatus, processedAt),
            cancellationToken);
    }

    [Fact]
    public async Task DeliverAsync_maps_failed_event_using_created_time_when_not_processed()
    {
        var message = NewMessage(Constants.TransactionFailed);
        message.ErrorReason = null;
        var strategy = new DaprOutboxDeliveryStrategy(_publisher.Object);

        await strategy.DeliverAsync(message, TestContext.Current.CancellationToken);

        VerifyPublished(
            message,
            new TransactionFailedEvent(
                message.TransactionId,
                message.TransactionStatus,
                new DateTimeOffset(message.CreatedAt),
                null),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeliverAsync_maps_balance_updated_event()
    {
        var message = NewMessage(Constants.BalanceUpdated);
        message.NewBalance = 125.50m;
        var strategy = new DaprOutboxDeliveryStrategy(_publisher.Object);

        await strategy.DeliverAsync(message, TestContext.Current.CancellationToken);

        VerifyPublished(
            message,
            new BalanceUpdatedEvent(
                message.TransactionId,
                message.AccountNumber,
                message.Amount,
                message.NewBalance.Value,
                message.Currency),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeliverAsync_when_balance_is_missing_throws_without_publishing()
    {
        var message = NewMessage(Constants.BalanceUpdated);
        message.NewBalance = null;
        var strategy = new DaprOutboxDeliveryStrategy(_publisher.Object);

        var act = () => strategy.DeliverAsync(message, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("BalanceUpdated event requires NewBalance.");
        _publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeliverAsync_when_event_type_is_unsupported_throws_without_publishing()
    {
        var message = NewMessage("com.corebank.unknown");
        var strategy = new DaprOutboxDeliveryStrategy(_publisher.Object);

        var act = () => strategy.DeliverAsync(message, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Unsupported event type 'com.corebank.unknown'.");
        _publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeliverAsync_when_publisher_throws_propagates_the_exception()
    {
        var message = NewMessage(Constants.TransactionCompleted);
        var expected = new InvalidOperationException("transport failed");
        _publisher
            .Setup(publisher => publisher.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(expected);
        var strategy = new DaprOutboxDeliveryStrategy(_publisher.Object);

        var act = () => strategy.DeliverAsync(message, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should().BeSameAs(expected);
    }

    private void VerifyPublished(
        MessagingOutboxMessage message,
        object expectedPayload,
        CancellationToken cancellationToken)
    {
        _publisher.Verify(publisher => publisher.PublishAsync(
            message.EventType,
            message.EventSource,
            message.TransactionId,
            It.Is<object>(payload => payload.Equals(expectedPayload)),
            message.TraceParent,
            cancellationToken), Times.Once);
        _publisher.VerifyNoOtherCalls();
    }

    private static MessagingOutboxMessage NewMessage(string eventType) => new()
    {
        Id = Guid.NewGuid(),
        PartitionId = 2,
        IdempotencyKey = "txn-123",
        Status = MessageConstants.Status.Pending,
        CreatedAt = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc),
        TraceParent = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01",
        TraceState = "congo=t61rcWkgMzE",
        TransactionId = "txn-123",
        EventType = eventType,
        EventSource = "https://corebank-api/transactions",
        AccountNumber = "NL91ABNA0417164300",
        ToAccount = "NL20INGB0001234567",
        Amount = 25.50m,
        Currency = "EUR",
        TransactionStatus = MessageConstants.Status.Completed
    };
}
