using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Controllers;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// Thin controller tests (spec-5-5's code map): each known-event action
/// completes only after <see cref="ITransactionEventIntakeHandler"/>'s
/// storage call finishes and then returns 200; the default/unknown route
/// never calls the handler, logs a structured warning, and still returns
/// 200 so Dapr acknowledges a type this service intentionally does not
/// recognize. No HTTP pipeline here -- the controller is constructed
/// directly against a mocked handler.
/// </summary>
public class TransactionEventsControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TransactionCompleted_awaits_storage_before_returning_ok()
    {
        var handler = new Mock<ITransactionEventIntakeHandler>(MockBehavior.Strict);
        var storeSignal = new TaskCompletionSource();
        var e = new TransactionCompletedEvent("txn-1", "Completed", Now);
        handler
            .Setup(h => h.StoreAsync(e, It.IsAny<CancellationToken>()))
            .Returns(storeSignal.Task);
        var controller = CreateController(handler.Object);

        var resultTask = controller.TransactionCompleted(e, TestContext.Current.CancellationToken);
        resultTask.IsCompleted.Should().BeFalse("the action must wait for storage before completing");

        storeSignal.SetResult();
        (await resultTask).Should().BeOfType<OkResult>();
        handler.Verify(h => h.StoreAsync(e, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TransactionFailed_awaits_storage_before_returning_ok()
    {
        var handler = new Mock<ITransactionEventIntakeHandler>(MockBehavior.Strict);
        var storeSignal = new TaskCompletionSource();
        var e = new TransactionFailedEvent("txn-2", "Failed", Now, "boom");
        handler.Setup(h => h.StoreAsync(e, It.IsAny<CancellationToken>())).Returns(storeSignal.Task);
        var controller = CreateController(handler.Object);

        var resultTask = controller.TransactionFailed(e, TestContext.Current.CancellationToken);
        resultTask.IsCompleted.Should().BeFalse("the action must wait for storage before completing");

        storeSignal.SetResult();
        (await resultTask).Should().BeOfType<OkResult>();
        handler.Verify(h => h.StoreAsync(e, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BalanceUpdated_awaits_storage_before_returning_ok()
    {
        var handler = new Mock<ITransactionEventIntakeHandler>(MockBehavior.Strict);
        var storeSignal = new TaskCompletionSource();
        var e = new BalanceUpdatedEvent("txn-3", "NL91ABNA0417164300", 1m, 2m, "EUR");
        handler.Setup(h => h.StoreAsync(e, It.IsAny<CancellationToken>())).Returns(storeSignal.Task);
        var controller = CreateController(handler.Object);

        var resultTask = controller.BalanceUpdated(e, TestContext.Current.CancellationToken);
        resultTask.IsCompleted.Should().BeFalse("the action must wait for storage before completing");

        storeSignal.SetResult();
        (await resultTask).Should().BeOfType<OkResult>();
        handler.Verify(h => h.StoreAsync(e, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Unknown_returns_ok_without_calling_the_handler_and_logs_a_warning()
    {
        var handler = new Mock<ITransactionEventIntakeHandler>(MockBehavior.Strict);
        var logger = new CapturingLogger();
        var controller = CreateController(handler.Object, logger);

        var result = controller.Unknown(
            "com.corebank.unknown.type",
            "event-unknown-1",
            "test-source");

        result.Should().BeOfType<OkResult>();
        handler.VerifyNoOtherCalls();
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("com.corebank.unknown.type", StringComparison.Ordinal) &&
            entry.Message.Contains("event-unknown-1", StringComparison.Ordinal) &&
            entry.Message.Contains("test-source", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_records_an_unknown_dapr_receive_delivery_metric_without_using_the_incoming_type_as_a_tag()
    {
        var handler = new Mock<ITransactionEventIntakeHandler>(MockBehavior.Strict);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var controller = CreateController(handler.Object, businessMetrics: businessMetrics);

        controller.Unknown("com.corebank.unknown.type", "event-unknown-1", "test-source");

        var measurement = listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == "corebankdemo.messaging.deliveries").Which;
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["messaging.direction"] = "received",
            ["messaging.transport"] = "dapr",
            ["messaging.message.type"] = "unknown",
            ["outcome"] = "unknown",
        });
    }

    [Fact]
    public async Task Cancellation_token_is_forwarded_unchanged_to_the_handler()
    {
        using var cts = new CancellationTokenSource();
        var handler = new Mock<ITransactionEventIntakeHandler>(MockBehavior.Strict);
        var e = new TransactionCompletedEvent("txn-4", "Completed", Now);
        handler.Setup(h => h.StoreAsync(e, cts.Token)).Returns(Task.CompletedTask);
        var controller = CreateController(handler.Object);

        await controller.TransactionCompleted(e, cts.Token);

        handler.VerifyAll();
    }

    [Fact]
    public async Task Storage_failure_propagates_instead_of_returning_ok()
    {
        var failure = new InvalidOperationException("store failed");
        var handler = new Mock<ITransactionEventIntakeHandler>(MockBehavior.Strict);
        var e = new TransactionCompletedEvent("txn-5", "Completed", Now);
        handler
            .Setup(h => h.StoreAsync(e, It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        var controller = CreateController(handler.Object);

        var act = () => controller.TransactionCompleted(e, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
    }

    private static TransactionEventsController CreateController(
        ITransactionEventIntakeHandler handler,
        ILogger<TransactionEventsController>? logger = null,
        BusinessMetrics? businessMetrics = null) =>
        new(handler, logger ?? new CapturingLogger(), businessMetrics ?? new BusinessMetrics());

    private sealed class CapturingLogger : ILogger<TransactionEventsController>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose()
            {
            }
        }
    }
}
