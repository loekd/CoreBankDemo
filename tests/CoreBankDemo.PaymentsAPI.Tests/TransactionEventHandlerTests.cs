using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// Exercises <see cref="TransactionEventHandler"/> against every spec-5-6 I/O
/// matrix row directly: typed dispatch by <see cref="Constants"/> value (not
/// CLR type), the approved structured log per event, the approved tags on
/// <see cref="Activity.Current"/> -- the consumer span
/// <see cref="CoreBankDemo.Messaging.InboxProcessorBase{TMessage}"/> already
/// restores, never a second <see cref="ActivitySource"/> created here --
/// malformed-payload and unsupported-type failures, and that handling never
/// mutates <see cref="InboxMessage"/> itself (kernel-owned completion).
/// </summary>
public class TransactionEventHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public async Task Completed_event_logs_information_and_tags_transaction_type_and_status()
    {
        using var observedActivity = StartListenedActivity();
        var activity = observedActivity.Activity;
        var payload = new TransactionCompletedEvent("txn-1", "Completed", Now);
        var message = Inbox(Constants.TransactionCompleted, "txn-1", payload: Serialize(payload));
        var logger = new CapturingLogger();
        var handler = new TransactionEventHandler(logger, new Mock<IOutboxRepository>().Object);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Information);
        logger.Entries.Single().Properties.Should().Contain(
            new KeyValuePair<string, object?>("TransactionId", "txn-1"),
            new KeyValuePair<string, object?>("Status", "Completed"),
            new KeyValuePair<string, object?>("EventType", Constants.TransactionCompleted));
        logger.Scopes.Single().Should().Contain(
            new KeyValuePair<string, object?>("IdempotencyKey", "txn-1"));
        logger.Scopes.Single().Should().Contain(
            new KeyValuePair<string, object?>("PartitionId", 0));
        activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("transaction.id", "txn-1"));
        activity.TagObjects.Should().Contain(
            new KeyValuePair<string, object?>("event.type", Constants.TransactionCompleted));
        activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("transaction.status", "Completed"));
    }

    [Fact]
    public async Task Failed_event_with_a_present_reason_logs_warning_and_tags_transaction_type_status_and_reason()
    {
        using var observedActivity = StartListenedActivity();
        var activity = observedActivity.Activity;
        var payload = new TransactionFailedEvent("txn-2", "Failed", Now, "Insufficient funds");
        var message = Inbox(Constants.TransactionFailed, "txn-2", payload: Serialize(payload));
        var logger = new CapturingLogger();
        var handler = new TransactionEventHandler(logger, new Mock<IOutboxRepository>().Object);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning);
        logger.Entries.Should().ContainSingle(entry => entry.Message.Contains("Insufficient funds"));
        logger.Entries.Single().Properties.Should().Contain(
            new KeyValuePair<string, object?>("TransactionId", "txn-2"),
            new KeyValuePair<string, object?>("Status", "Failed"),
            new KeyValuePair<string, object?>("ErrorReason", "Insufficient funds"),
            new KeyValuePair<string, object?>("EventType", Constants.TransactionFailed));
        activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("transaction.id", "txn-2"));
        activity.TagObjects.Should().Contain(
            new KeyValuePair<string, object?>("event.type", Constants.TransactionFailed));
        activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("transaction.status", "Failed"));
        activity.TagObjects.Should().Contain(
            new KeyValuePair<string, object?>("transaction.error_reason", "Insufficient funds"));
    }

    [Fact]
    public async Task Failed_event_with_a_null_reason_remains_valid_and_still_logs_warning()
    {
        using var observedActivity = StartListenedActivity();
        var activity = observedActivity.Activity;
        var payload = new TransactionFailedEvent("txn-3", "Failed", Now, null);
        var message = Inbox(Constants.TransactionFailed, "txn-3", payload: Serialize(payload));
        var logger = new CapturingLogger();
        var handler = new TransactionEventHandler(logger, new Mock<IOutboxRepository>().Object);

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning);
        activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("transaction.id", "txn-3"));
        activity.TagObjects.Should().Contain(
            new KeyValuePair<string, object?>("transaction.error_reason", string.Empty));
    }

    [Fact]
    public async Task Balance_update_logs_information_and_tags_transaction_account_delta_new_balance_and_currency()
    {
        using var observedActivity = StartListenedActivity();
        var activity = observedActivity.Activity;
        var payload = new BalanceUpdatedEvent("txn-4", "NL91ABNA0417164300", -12.34m, 987.66m, "EUR");
        var message = Inbox(
            Constants.BalanceUpdated,
            "txn-4",
            accountNumber: "NL91ABNA0417164300",
            payload: Serialize(payload));
        var logger = new CapturingLogger();
        var handler = new TransactionEventHandler(logger, new Mock<IOutboxRepository>().Object);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Information);
        logger.Entries.Single().Properties.Should().Contain(
            new KeyValuePair<string, object?>("AccountNumber", "NL91ABNA0417164300"),
            new KeyValuePair<string, object?>("Delta", -12.34m),
            new KeyValuePair<string, object?>("NewBalance", 987.66m),
            new KeyValuePair<string, object?>("Currency", "EUR"),
            new KeyValuePair<string, object?>("TransactionId", "txn-4"),
            new KeyValuePair<string, object?>("EventType", Constants.BalanceUpdated));
        activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("transaction.id", "txn-4"));
        activity.TagObjects.Should().Contain(
            new KeyValuePair<string, object?>("event.type", Constants.BalanceUpdated));
        activity.TagObjects.Should().Contain(
            new KeyValuePair<string, object?>("account.number", "NL91ABNA0417164300"));
        activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("account.delta", -12.34m));
        activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("account.new_balance", 987.66m));
        activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("account.currency", "EUR"));
    }

    [Fact]
    public async Task Redelivery_of_an_already_claimed_row_repeats_safely_with_no_local_state_changes()
    {
        var payload = new TransactionCompletedEvent("txn-5", "Completed", Now);
        var message = Inbox(Constants.TransactionCompleted, "txn-5", payload: Serialize(payload));
        message.Status = MessageConstants.Status.Processing;
        var handler = new TransactionEventHandler(new CapturingLogger(), new Mock<IOutboxRepository>().Object);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);
        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        message.Status.Should().Be(MessageConstants.Status.Processing);
        message.Payload.Should().Be(Serialize(payload));
    }

    [Fact]
    public async Task Invalid_json_throws_JsonException_so_the_kernel_records_retry()
    {
        var message = Inbox(Constants.TransactionCompleted, "txn-6", payload: "{not-json");
        var handler = new TransactionEventHandler(new CapturingLogger(), new Mock<IOutboxRepository>().Object);

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task Json_null_throws_InvalidOperationException_so_the_kernel_records_retry()
    {
        var message = Inbox(Constants.TransactionCompleted, "txn-6", payload: "null");
        var handler = new TransactionEventHandler(new CapturingLogger(), new Mock<IOutboxRepository>().Object);

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("payload deserialized to null");
    }

    [Fact]
    public async Task Missing_required_payload_fields_throw_JsonException_so_the_kernel_records_retry()
    {
        var message = Inbox(Constants.TransactionCompleted, "txn-6", payload: "{}");
        var handler = new TransactionEventHandler(new CapturingLogger(), new Mock<IOutboxRepository>().Object);

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task Explicit_null_for_a_non_nullable_payload_field_throws_JsonException()
    {
        var message = Inbox(
            Constants.TransactionCompleted,
            "txn-6",
            payload: """{"transactionId":null,"status":"Completed","processedAt":"2026-08-29T12:00:00Z"}""");
        var handler = new TransactionEventHandler(new CapturingLogger(), new Mock<IOutboxRepository>().Object);

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task Unsupported_stored_event_type_throws_an_explicit_unsupported_type_error()
    {
        var message = Inbox("com.corebank.unknown.type", "txn-7", payload: "{}");
        var handler = new TransactionEventHandler(new CapturingLogger(), new Mock<IOutboxRepository>().Object);

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("com.corebank.unknown.type");
    }

    [Fact]
    public async Task Host_cancellation_propagates_before_dispatch()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var message = Inbox(
            Constants.TransactionCompleted,
            "txn-8",
            payload: Serialize(new TransactionCompletedEvent("txn-8", "Completed", Now)));
        message.Status = MessageConstants.Status.Processing;
        var handler = new TransactionEventHandler(new CapturingLogger(), new Mock<IOutboxRepository>().Object);

        var act = () => handler.HandleAsync(message, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        message.Status.Should().Be(MessageConstants.Status.Processing);
    }

    // ---- The instant rail's deferred-then-settled gap: CoreBank's inline
    // attempt regularly answers 202/Pending, that non-committed answer gets
    // cached on the payment row, and nothing ever refreshed it -- every
    // duplicate replay said Pending forever for a payment that had settled.
    // The transaction event is where the payment learns its real outcome. ----

    [Theory]
    [InlineData(Constants.TransactionCompleted, "Completed")]
    [InlineData(Constants.TransactionFailed, "Failed")]
    [InlineData(Constants.TransactionCancelled, "Cancelled")]
    public async Task Committed_outcome_events_are_recorded_on_the_payment_row(string eventType, string status)
    {
        var repository = new Mock<IOutboxRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.RecordCommittedOutcomeAsync("txn-9", status, Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var payload = eventType switch
        {
            Constants.TransactionCompleted => Serialize(new TransactionCompletedEvent("txn-9", status, Now)),
            Constants.TransactionFailed => Serialize(new TransactionFailedEvent("txn-9", status, Now, "Insufficient funds")),
            // spec: instant-rail-cancelled-event -- a cancellation CoreBank
            // committed is the residual 202's committed outcome.
            _ => Serialize(new TransactionCancelledEvent("txn-9", status, Now, "budget exhausted")),
        };
        var message = Inbox(eventType, "txn-9", payload: payload);
        var logger = new CapturingLogger();
        var handler = new TransactionEventHandler(logger, repository.Object);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        repository.VerifyAll();
        logger.Entries.Should().Contain(entry => entry.Message.Contains("Recorded committed outcome"));
    }

    [Fact]
    public async Task Cancelled_event_logs_information_and_tags_transaction_type_status_and_reason()
    {
        using var observedActivity = StartListenedActivity();
        var activity = observedActivity.Activity;
        var payload = new TransactionCancelledEvent("txn-2c", "Cancelled", Now, "Cancelled by the instant rail on budget exhaustion");
        var message = Inbox(Constants.TransactionCancelled, "txn-2c", payload: Serialize(payload));
        var logger = new CapturingLogger();
        var handler = new TransactionEventHandler(logger, new Mock<IOutboxRepository>().Object);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Information);
        logger.Entries.Single().Properties.Should().Contain(
            new KeyValuePair<string, object?>("TransactionId", "txn-2c"),
            new KeyValuePair<string, object?>("Status", "Cancelled"),
            new KeyValuePair<string, object?>("Reason", "Cancelled by the instant rail on budget exhaustion"),
            new KeyValuePair<string, object?>("EventType", Constants.TransactionCancelled));
        activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("transaction.id", "txn-2c"));
        activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("event.type", Constants.TransactionCancelled));
        activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("transaction.status", "Cancelled"));
        activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("transaction.cancel_reason", "Cancelled by the instant rail on budget exhaustion"));
    }

    [Fact]
    public async Task Cancelled_event_with_a_null_reason_remains_valid_and_tags_an_empty_reason()
    {
        using var observedActivity = StartListenedActivity();
        var activity = observedActivity.Activity;
        var payload = new TransactionCancelledEvent("txn-2d", "Cancelled", Now, null);
        var message = Inbox(Constants.TransactionCancelled, "txn-2d", payload: Serialize(payload));
        var handler = new TransactionEventHandler(new CapturingLogger(), new Mock<IOutboxRepository>().Object);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("transaction.cancel_reason", string.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Completed")]
    [InlineData("cancelled")]
    public async Task A_cancelled_event_whose_status_is_not_the_wire_word_Cancelled_throws_and_never_touches_the_payment_row(string status)
    {
        var repository = new Mock<IOutboxRepository>(MockBehavior.Strict);
        var message = Inbox(Constants.TransactionCancelled, "txn-2f", payload: Serialize(new TransactionCancelledEvent("txn-2f", status, Now, null)));
        var handler = new TransactionEventHandler(new CapturingLogger(), repository.Object);

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("txn-2f").And.Contain("Cancelled");
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_cancelled_event_with_a_null_status_throws_so_the_kernel_retries_and_never_touches_the_payment_row()
    {
        // Status is a non-nullable contract field: strict deserialization
        // rejects an explicit null before the handler's own check can run.
        // Either way the kernel records the retry and the row is untouched.
        var repository = new Mock<IOutboxRepository>(MockBehavior.Strict);
        var message = Inbox(
            Constants.TransactionCancelled,
            "txn-2g",
            payload: """{"transactionId":"txn-2g","status":null,"processedAt":"2026-08-29T12:34:56+00:00","reason":null}""");
        var handler = new TransactionEventHandler(new CapturingLogger(), repository.Object);

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<Exception>().Where(e => e is JsonException || e is InvalidOperationException);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_cancelled_event_after_the_rail_already_marked_the_row_cancelled_is_a_silent_no_op()
    {
        // Matrix: "Event after the rail already marked Cancelled" -- the
        // repository refuses to overwrite a terminal cached outcome and reports
        // false; the handler logs nothing about recording.
        var repository = new Mock<IOutboxRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.RecordCommittedOutcomeAsync("txn-2e", "Cancelled", Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var message = Inbox(Constants.TransactionCancelled, "txn-2e", payload: Serialize(new TransactionCancelledEvent("txn-2e", "Cancelled", Now, null)));
        var logger = new CapturingLogger();
        var handler = new TransactionEventHandler(logger, repository.Object);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        repository.VerifyAll();
        logger.Entries.Should().NotContain(entry => entry.Message.Contains("Recorded committed outcome"));
    }

    [Fact]
    public async Task Balance_events_never_touch_the_payment_row()
    {
        var repository = new Mock<IOutboxRepository>(MockBehavior.Strict);
        var payload = new BalanceUpdatedEvent("txn-10", "NL91ABNA0417164300", -25m, 975m, "EUR");
        var message = Inbox(Constants.BalanceUpdated, "txn-10", "NL91ABNA0417164300", Serialize(payload));
        var handler = new TransactionEventHandler(new CapturingLogger(), repository.Object);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_failed_outcome_write_propagates_so_the_kernel_retries_the_event()
    {
        // Losing the concurrency race against the outbox processor must not
        // silently acknowledge the event -- the outcome would be lost forever.
        var repository = new Mock<IOutboxRepository>();
        repository
            .Setup(r => r.RecordCommittedOutcomeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("raced the outbox processor"));
        var message = Inbox(
            Constants.TransactionCompleted,
            "txn-11",
            payload: Serialize(new TransactionCompletedEvent("txn-11", "Completed", Now)));
        var handler = new TransactionEventHandler(new CapturingLogger(), repository.Object);

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException>();
    }

    private static ObservedActivity StartListenedActivity()
    {
        var activitySource = new ActivitySource(nameof(TransactionEventHandlerTests) + Guid.NewGuid());
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == activitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        var activity = activitySource.StartActivity("ProcessInboxMessage", ActivityKind.Consumer);
        activity.Should().NotBeNull();
        return new ObservedActivity(activitySource, listener, activity!);
    }

    private static string Serialize<TEvent>(TEvent payload) => JsonSerializer.Serialize(payload);

    private static InboxMessage Inbox(
        string eventType,
        string transactionId,
        string accountNumber = "",
        string payload = "{}") => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = transactionId,
        TransactionId = transactionId,
        EventType = eventType,
        AccountNumber = accountNumber,
        Payload = payload,
        PartitionId = 0,
        Status = MessageConstants.Status.Pending,
        ReceivedAt = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc)
    };

    private sealed class CapturingLogger : ILogger<TransactionEventHandler>
    {
        public List<LogEntry> Entries { get; } = [];
        public List<IReadOnlyList<KeyValuePair<string, object?>>> Scopes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                Scopes.Add(values.ToArray());
            }

            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.Where(pair => pair.Key != "{OriginalFormat}").ToArray()
                : [];
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), properties));
        }

        public sealed record LogEntry(
            LogLevel Level,
            string Message,
            IReadOnlyList<KeyValuePair<string, object?>> Properties);

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose()
            {
            }
        }
    }

    private sealed class ObservedActivity(
        ActivitySource source,
        ActivityListener listener,
        Activity activity) : IDisposable
    {
        public Activity Activity { get; } = activity;

        public void Dispose()
        {
            Activity.Dispose();
            listener.Dispose();
            source.Dispose();
        }
    }
}
