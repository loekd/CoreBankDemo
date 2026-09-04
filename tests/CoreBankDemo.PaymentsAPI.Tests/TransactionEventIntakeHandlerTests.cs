using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// Exercises <see cref="TransactionEventIntakeHandler"/> against a mocked
/// <see cref="IInboxMessageRepository"/> (spec-5-5's code map): pins every
/// known event's exact composite identity/partition mapping, the empty
/// account sentinel for transaction-wide events, payload serialization
/// (including a preserved nullable error reason), injected time, ambient
/// trace capture, exact cancellation-token forwarding, and structured
/// duplicate logging.
/// </summary>
public class TransactionEventIntakeHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public async Task TransactionCompleted_stores_under_the_empty_account_sentinel_with_the_transaction_id_as_partition_key()
    {
        InboxMessage? captured = null;
        var repository = new Mock<IInboxMessageRepository>();
        repository
            .Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((message, _) => captured = message)
            .ReturnsAsync(true);
        var handler = CreateHandler(repository.Object);
        var e = new TransactionCompletedEvent("txn-1", "Completed", Now);

        await handler.StoreAsync(e, TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.IdempotencyKey.Should().Be("txn-1");
        captured.TransactionId.Should().Be("txn-1");
        captured.EventType.Should().Be(Constants.TransactionCompleted);
        captured.AccountNumber.Should().Be("");
        captured.PartitionId.Should().Be(PartitionHelper.GetPartitionId("txn-1", 4));
        captured.Status.Should().Be(MessageConstants.Status.Pending);
        captured.ReceivedAt.Should().Be(Now.UtcDateTime);
        JsonSerializer.Deserialize<TransactionCompletedEvent>(captured.Payload).Should().Be(e);
    }

    [Fact]
    public async Task TransactionFailed_preserves_a_null_error_reason_in_the_serialized_payload()
    {
        InboxMessage? captured = null;
        var repository = new Mock<IInboxMessageRepository>();
        repository
            .Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((message, _) => captured = message)
            .ReturnsAsync(true);
        var handler = CreateHandler(repository.Object);
        var e = new TransactionFailedEvent("txn-2", "Failed", Now, null);

        await handler.StoreAsync(e, TestContext.Current.CancellationToken);

        captured!.IdempotencyKey.Should().Be("txn-2");
        captured.TransactionId.Should().Be("txn-2");
        captured.EventType.Should().Be(Constants.TransactionFailed);
        captured.AccountNumber.Should().Be("");
        captured.PartitionId.Should().Be(PartitionHelper.GetPartitionId("txn-2", 4));
        var deserialized = JsonSerializer.Deserialize<TransactionFailedEvent>(captured.Payload);
        deserialized.Should().Be(e);
        deserialized!.ErrorReason.Should().BeNull();
    }

    [Fact]
    public async Task TransactionFailed_preserves_a_present_error_reason_in_the_serialized_payload()
    {
        InboxMessage? captured = null;
        var repository = new Mock<IInboxMessageRepository>();
        repository
            .Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((message, _) => captured = message)
            .ReturnsAsync(true);
        var handler = CreateHandler(repository.Object);
        var e = new TransactionFailedEvent("txn-3", "Failed", Now, "Insufficient funds");

        await handler.StoreAsync(e, TestContext.Current.CancellationToken);

        JsonSerializer.Deserialize<TransactionFailedEvent>(captured!.Payload)!.ErrorReason
            .Should().Be("Insufficient funds");
    }

    [Fact]
    public async Task BalanceUpdated_stores_under_its_own_account_number_with_the_transaction_id_as_partition_key()
    {
        InboxMessage? captured = null;
        var repository = new Mock<IInboxMessageRepository>();
        repository
            .Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((message, _) => captured = message)
            .ReturnsAsync(true);
        var handler = CreateHandler(repository.Object);
        var e = new BalanceUpdatedEvent("txn-4", "NL91ABNA0417164300", -12.34m, 987.66m, "EUR");

        await handler.StoreAsync(e, TestContext.Current.CancellationToken);

        captured!.IdempotencyKey.Should().Be("txn-4");
        captured.TransactionId.Should().Be("txn-4");
        captured.EventType.Should().Be(Constants.BalanceUpdated);
        captured.AccountNumber.Should().Be("NL91ABNA0417164300");
        captured.PartitionId.Should().Be(PartitionHelper.GetPartitionId("txn-4", 4));
        JsonSerializer.Deserialize<BalanceUpdatedEvent>(captured.Payload).Should().Be(e);
    }

    [Fact]
    public async Task Two_balance_events_for_the_same_transaction_but_different_accounts_are_distinct_rows()
    {
        var repository = new Mock<IInboxMessageRepository>();
        repository
            .Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var handler = CreateHandler(repository.Object);

        await handler.StoreAsync(
            new BalanceUpdatedEvent("txn-5", "NL91ABNA0417164300", -1m, 99m, "EUR"),
            TestContext.Current.CancellationToken);
        await handler.StoreAsync(
            new BalanceUpdatedEvent("txn-5", "NL20INGB0001234567", 1m, 101m, "EUR"),
            TestContext.Current.CancellationToken);

        repository.Verify(
            r => r.StoreIfNewAsync(
                It.Is<InboxMessage>(m => m.AccountNumber == "NL91ABNA0417164300"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        repository.Verify(
            r => r.StoreIfNewAsync(
                It.Is<InboxMessage>(m => m.AccountNumber == "NL20INGB0001234567"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task No_ambient_activity_persists_null_trace_fields()
    {
        Activity.Current.Should().BeNull();
        InboxMessage? captured = null;
        var repository = new Mock<IInboxMessageRepository>();
        repository
            .Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((message, _) => captured = message)
            .ReturnsAsync(true);
        var handler = CreateHandler(repository.Object);

        await handler.StoreAsync(
            new TransactionCompletedEvent("txn-6", "Completed", Now),
            TestContext.Current.CancellationToken);

        captured!.TraceParent.Should().BeNull();
        captured.TraceState.Should().BeNull();
    }

    [Fact]
    public async Task Ambient_activity_is_captured_into_trace_fields()
    {
        using var activitySource = new ActivitySource(nameof(Ambient_activity_is_captured_into_trace_fields));
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = activitySource.StartActivity("test-activity");
        activity.Should().NotBeNull();

        InboxMessage? captured = null;
        var repository = new Mock<IInboxMessageRepository>();
        repository
            .Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((message, _) => captured = message)
            .ReturnsAsync(true);
        var handler = CreateHandler(repository.Object);

        await handler.StoreAsync(
            new TransactionCompletedEvent("txn-7", "Completed", Now),
            TestContext.Current.CancellationToken);

        captured!.TraceParent.Should().Be(activity!.Id);
        captured.TraceState.Should().Be(activity.TraceStateString);
    }

    [Fact]
    public async Task Duplicate_delivery_logs_structured_identity_without_throwing()
    {
        var repository = new Mock<IInboxMessageRepository>();
        repository
            .Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var logger = new CapturingLogger();
        var handler = CreateHandler(repository.Object, logger);

        var act = () => handler.StoreAsync(
            new TransactionCompletedEvent("txn-8", "Completed", Now),
            TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        logger.Scope.Should().Contain(new KeyValuePair<string, object>("TransactionId", "txn-8"));
        logger.Scope.Should().Contain(new KeyValuePair<string, object>("EventType", Constants.TransactionCompleted));
        logger.Scope.Should().Contain(new KeyValuePair<string, object>("AccountNumber", ""));
        logger.Messages.Should().ContainSingle(message => message.Contains("Duplicate"));
    }

    // ---- Story 6.5: business metrics ----

    [Fact]
    public async Task TransactionCompleted_records_a_succeeded_dapr_receive_delivery_metric_when_newly_stored()
    {
        var repository = new Mock<IInboxMessageRepository>();
        repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var handler = CreateHandler(repository.Object, businessMetrics: businessMetrics);

        await handler.StoreAsync(new TransactionCompletedEvent("txn-100", "Completed", Now), TestContext.Current.CancellationToken);

        var measurement = listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == "corebankdemo.messaging.deliveries").Which;
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["messaging.direction"] = "received",
            ["messaging.transport"] = "dapr",
            ["messaging.message.type"] = "transaction-completed",
            ["outcome"] = "succeeded",
        });
    }

    [Theory]
    [InlineData(true, "succeeded")]
    [InlineData(false, "duplicate")]
    public async Task BalanceUpdated_records_the_dapr_receive_delivery_outcome_matching_the_store_result(
        bool stored, string expectedOutcome)
    {
        var repository = new Mock<IInboxMessageRepository>();
        repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var handler = CreateHandler(repository.Object, businessMetrics: businessMetrics);

        await handler.StoreAsync(
            new BalanceUpdatedEvent("txn-101", "NL91ABNA0417164300", 1m, 2m, "EUR"), TestContext.Current.CancellationToken);

        var measurement = listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == "corebankdemo.messaging.deliveries").Which;
        measurement.Tags["messaging.message.type"].Should().Be("balance-updated");
        measurement.Tags["outcome"].Should().Be(expectedOutcome);
    }

    [Fact]
    public async Task Cancellation_token_is_forwarded_unchanged_to_the_repository()
    {
        using var cts = new CancellationTokenSource();
        var repository = new Mock<IInboxMessageRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), cts.Token))
            .ReturnsAsync(true);
        var handler = CreateHandler(repository.Object);

        await handler.StoreAsync(new TransactionCompletedEvent("txn-9", "Completed", Now), cts.Token);

        repository.VerifyAll();
    }

    [Fact]
    public async Task Persistence_failure_propagates_unchanged()
    {
        var failure = new InvalidOperationException("database unavailable");
        var repository = new Mock<IInboxMessageRepository>();
        repository
            .Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var handler = CreateHandler(repository.Object, businessMetrics: businessMetrics);

        var act = () => handler.StoreAsync(
            new TransactionCompletedEvent("txn-10", "Completed", Now),
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.MessagingDeliveriesInstrumentName)
            .Which.Tags["outcome"].Should().Be("failed");
    }

    [Fact]
    public async Task Repository_cancellation_propagates_unchanged()
    {
        var cancellation = new OperationCanceledException();
        var repository = new Mock<IInboxMessageRepository>();
        repository
            .Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(cancellation);
        var handler = CreateHandler(repository.Object);

        var act = () => handler.StoreAsync(
            new TransactionCompletedEvent("txn-11", "Completed", Now),
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(cancellation);
    }

    private static TransactionEventIntakeHandler CreateHandler(
        IInboxMessageRepository repository,
        ILogger<TransactionEventIntakeHandler>? logger = null,
        BusinessMetrics? businessMetrics = null) =>
        new(
            repository,
            Options.Create(new InboxProcessingOptions
            {
                PartitionCount = 4,
                LockExpirySeconds = 30,
                PollingIntervalMs = 200
            }),
            new FixedTimeProvider(Now),
            logger ?? NullLogger<TransactionEventIntakeHandler>.Instance,
            businessMetrics ?? new BusinessMetrics());

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingLogger : ILogger<TransactionEventIntakeHandler>
    {
        public IReadOnlyCollection<KeyValuePair<string, object>> Scope { get; private set; } = [];
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Scope = (IReadOnlyCollection<KeyValuePair<string, object>>)state;
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose()
            {
            }
        }
    }
}
