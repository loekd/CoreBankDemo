using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Accounts;
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
/// malformed-payload and unsupported-type failures, the event's effect on the
/// account projection, and that the handler completes its own inbox row
/// through <see cref="IInboxMessageRepository.MarkAsCompletedAsync"/> inside
/// the same transaction (transactional inbox, spec: payments-account-projection).
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
        var handler = CreateHandler(logger);

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
        var handler = CreateHandler(logger);

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

    [Theory]
    [InlineData("Insufficient funds", "Insufficient funds")]
    [InlineData(null, "")]
    public async Task Failed_event_tags_the_span_as_a_rejected_payment_with_its_reason(string? errorReason, string expectedReason)
    {
        // The transaction.failed event is the only place PaymentsAPI learns
        // why CoreBank rejected a payment, so this span carries the traces
        // dashboard's "Failed payments" tags for a rejection.
        using var observedActivity = StartListenedActivity();
        var activity = observedActivity.Activity;
        var payload = new TransactionFailedEvent("txn-2", "Failed", Now, errorReason);
        var message = Inbox(Constants.TransactionFailed, "txn-2", payload: Serialize(payload));
        var handler = CreateHandler();

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        activity.TagObjects.Should().Contain(
            new KeyValuePair<string, object?>(FailedPaymentTags.Outcome, FailedPaymentTags.Rejected),
            new KeyValuePair<string, object?>(FailedPaymentTags.FailureReason, expectedReason),
            new KeyValuePair<string, object?>(FailedPaymentTags.TransactionId, "txn-2"));
    }

    [Fact]
    public async Task Completed_and_cancelled_events_never_tag_a_failed_payment()
    {
        // A settlement is not a failure, and a cancellation is tagged where
        // the outbox row is cancelled, never a second time here.
        using var observedActivity = StartListenedActivity();
        var activity = observedActivity.Activity;
        var handler = CreateHandler();

        await handler.HandleAsync(
            Inbox(Constants.TransactionCompleted, "txn-1", payload: Serialize(new TransactionCompletedEvent("txn-1", "Completed", Now))),
            TestContext.Current.CancellationToken);
        await handler.HandleAsync(
            Inbox(Constants.TransactionCancelled, "txn-4", payload: Serialize(new TransactionCancelledEvent("txn-4", "Cancelled", Now, "budget"))),
            TestContext.Current.CancellationToken);

        activity.TagObjects.Should().NotContain(tag => tag.Key == FailedPaymentTags.Outcome);
    }

    [Fact]
    public async Task Failed_event_with_a_null_reason_remains_valid_and_still_logs_warning()
    {
        using var observedActivity = StartListenedActivity();
        var activity = observedActivity.Activity;
        var payload = new TransactionFailedEvent("txn-3", "Failed", Now, null);
        var message = Inbox(Constants.TransactionFailed, "txn-3", payload: Serialize(payload));
        var logger = new CapturingLogger();
        var handler = CreateHandler(logger);

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
        var handler = CreateHandler(logger);

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
        var inbox = TransactionalInbox();
        var handler = CreateHandler(inbox: inbox);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);
        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        // Completion is the handler's job now; the repository's own terminal
        // guard makes the second delivery's call a no-op.
        inbox.Verify(r => r.MarkAsCompletedAsync(message, It.IsAny<CancellationToken>()), Times.Exactly(2));
        message.Payload.Should().Be(Serialize(payload));
    }

    [Fact]
    public async Task Invalid_json_throws_JsonException_so_the_kernel_records_retry()
    {
        var message = Inbox(Constants.TransactionCompleted, "txn-6", payload: "{not-json");
        var handler = CreateHandler();

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task Json_null_throws_InvalidOperationException_so_the_kernel_records_retry()
    {
        var message = Inbox(Constants.TransactionCompleted, "txn-6", payload: "null");
        var handler = CreateHandler();

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("payload deserialized to null");
    }

    [Fact]
    public async Task Missing_required_payload_fields_throw_JsonException_so_the_kernel_records_retry()
    {
        var message = Inbox(Constants.TransactionCompleted, "txn-6", payload: "{}");
        var handler = CreateHandler();

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
        var handler = CreateHandler();

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task Unsupported_stored_event_type_throws_an_explicit_unsupported_type_error()
    {
        var message = Inbox("com.corebank.unknown.type", "txn-7", payload: "{}");
        var handler = CreateHandler();

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
        var handler = CreateHandler();

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
        if (eventType != Constants.TransactionCompleted)
        {
            // Failed/cancelled also look the payment up to release its reservation.
            repository
                .Setup(r => r.FindByIdempotencyKeyAsync("txn-9", It.IsAny<CancellationToken>()))
                .ReturnsAsync((OutboxMessage?)null);
        }
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
        var handler = CreateHandler(logger, repository);

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
        var handler = CreateHandler(logger);

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
        var handler = CreateHandler();

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
        var handler = CreateHandler(outbox: repository);

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
        var handler = CreateHandler(outbox: repository);

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
        repository
            .Setup(r => r.FindByIdempotencyKeyAsync("txn-2e", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OutboxMessage?)null);
        var message = Inbox(Constants.TransactionCancelled, "txn-2e", payload: Serialize(new TransactionCancelledEvent("txn-2e", "Cancelled", Now, null)));
        var logger = new CapturingLogger();
        var handler = CreateHandler(logger, repository);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        repository.VerifyAll();
        logger.Entries.Should().NotContain(entry => entry.Message.Contains("Recorded committed outcome"));
    }

    [Fact]
    public async Task Balance_events_never_touch_the_payment_row()
    {
        var repository = new Mock<IOutboxRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.FindByIdempotencyKeyAsync("txn-10", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentsApiTestData.Outbox("txn-10"));
        var payload = new BalanceUpdatedEvent("txn-10", "NL91ABNA0417164300", -25m, 975m, "EUR");
        var message = Inbox(Constants.BalanceUpdated, "txn-10", "NL91ABNA0417164300", Serialize(payload));
        var handler = CreateHandler(outbox: repository);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        // The payment is only read (to find the reservation to release), never written.
        repository.Verify(r => r.FindByIdempotencyKeyAsync("txn-10", It.IsAny<CancellationToken>()), Times.Once);
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
        var handler = CreateHandler(outbox: repository);

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task Balance_update_for_the_debtor_settles_and_releases_the_payment_amount_inside_the_transaction()
    {
        var accounts = new Mock<IAccountProjectionStore>(MockBehavior.Strict);
        var outbox = new Mock<IOutboxRepository>();
        var inbox = TransactionalInbox();
        var calls = new List<string>();
        outbox
            .Setup(r => r.FindByIdempotencyKeyAsync("txn-4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentsApiTestData.Outbox("txn-4")); // FromAccount NL91…, Amount 12.34
        accounts
            .Setup(a => a.SettleAsync("NL91ABNA0417164300", 987.66m, "EUR", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("settle")).Returns(Task.CompletedTask);
        accounts
            .Setup(a => a.ReleaseAsync("NL91ABNA0417164300", 12.34m, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("release")).ReturnsAsync(0m);
        inbox
            .Setup(r => r.MarkAsCompletedAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("complete")).ReturnsAsync(MessageTransitionOutcome.Applied);
        var message = Inbox(Constants.BalanceUpdated, "txn-4", accountNumber: "NL91ABNA0417164300",
            payload: Serialize(new BalanceUpdatedEvent("txn-4", "NL91ABNA0417164300", -12.34m, 987.66m, "EUR")));
        var handler = CreateHandler(outbox: outbox, accounts: accounts, inbox: inbox);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        calls.Should().Equal("settle", "release", "complete");
        accounts.VerifyAll();
    }

    [Fact]
    public async Task Balance_update_for_the_creditor_only_settles()
    {
        var accounts = new Mock<IAccountProjectionStore>(MockBehavior.Strict);
        var outbox = new Mock<IOutboxRepository>();
        outbox
            .Setup(r => r.FindByIdempotencyKeyAsync("txn-4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentsApiTestData.Outbox("txn-4")); // debtor is NL91…, this event is for NL20…
        accounts
            .Setup(a => a.SettleAsync("NL20INGB0001234567", 1012.34m, "EUR", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var message = Inbox(Constants.BalanceUpdated, "txn-4", accountNumber: "NL20INGB0001234567",
            payload: Serialize(new BalanceUpdatedEvent("txn-4", "NL20INGB0001234567", 12.34m, 1012.34m, "EUR")));
        var handler = CreateHandler(outbox: outbox, accounts: accounts);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        accounts.Verify(a => a.ReleaseAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Balance_update_for_an_unknown_transaction_only_settles()
    {
        var accounts = new Mock<IAccountProjectionStore>(MockBehavior.Strict);
        var outbox = new Mock<IOutboxRepository>();
        outbox
            .Setup(r => r.FindByIdempotencyKeyAsync("someone-elses", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OutboxMessage?)null);
        accounts
            .Setup(a => a.SettleAsync("NL39RABO0300065264", 2500m, "EUR", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var message = Inbox(Constants.BalanceUpdated, "someone-elses", accountNumber: "NL39RABO0300065264",
            payload: Serialize(new BalanceUpdatedEvent("someone-elses", "NL39RABO0300065264", 5m, 2500m, "EUR")));
        var handler = CreateHandler(outbox: outbox, accounts: accounts);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        accounts.VerifyAll();
    }

    [Theory]
    [InlineData(Constants.TransactionFailed)]
    [InlineData(Constants.TransactionCancelled)]
    public async Task Failed_and_cancelled_release_the_debtor_reservation_and_record_the_outcome(string eventType)
    {
        var accounts = new Mock<IAccountProjectionStore>(MockBehavior.Strict);
        var outbox = new Mock<IOutboxRepository>();
        outbox
            .Setup(r => r.FindByIdempotencyKeyAsync("txn-r", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentsApiTestData.Outbox("txn-r"));
        outbox
            .Setup(r => r.RecordCommittedOutcomeAsync("txn-r", It.IsAny<string>(), Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        accounts
            .Setup(a => a.ReleaseAsync("NL91ABNA0417164300", 12.34m, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        var payload = eventType == Constants.TransactionFailed
            ? Serialize(new TransactionFailedEvent("txn-r", "Failed", Now, "Insufficient funds"))
            : Serialize(new TransactionCancelledEvent("txn-r", "Cancelled", Now, "budget"));
        var handler = CreateHandler(outbox: outbox, accounts: accounts);

        await handler.HandleAsync(Inbox(eventType, "txn-r", payload: payload), TestContext.Current.CancellationToken);

        accounts.VerifyAll();
        outbox.Verify(r => r.RecordCommittedOutcomeAsync("txn-r", It.IsAny<string>(), Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Completed_event_touches_no_account()
    {
        var accounts = new Mock<IAccountProjectionStore>(MockBehavior.Strict);
        var handler = CreateHandler(accounts: accounts);

        await handler.HandleAsync(
            Inbox(Constants.TransactionCompleted, "txn-1", payload: Serialize(new TransactionCompletedEvent("txn-1", "Completed", Now))),
            TestContext.Current.CancellationToken);

        accounts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Release_below_zero_clamps_and_warns()
    {
        var accounts = new Mock<IAccountProjectionStore>();
        var outbox = new Mock<IOutboxRepository>();
        outbox
            .Setup(r => r.FindByIdempotencyKeyAsync("txn-old", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentsApiTestData.Outbox("txn-old"));
        accounts
            .Setup(a => a.ReleaseAsync("NL91ABNA0417164300", 12.34m, It.IsAny<CancellationToken>()))
            .ReturnsAsync(12.34m); // nothing was reserved: projection younger than the payment
        var logger = new CapturingLogger();
        var message = Inbox(Constants.BalanceUpdated, "txn-old", accountNumber: "NL91ABNA0417164300",
            payload: Serialize(new BalanceUpdatedEvent("txn-old", "NL91ABNA0417164300", -12.34m, 100m, "EUR")));
        var handler = CreateHandler(logger, outbox, accounts);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("12.34"));
    }

    [Fact]
    public async Task A_failing_projection_write_propagates_and_never_completes_the_row()
    {
        var accounts = new Mock<IAccountProjectionStore>();
        var inbox = TransactionalInbox();
        accounts
            .Setup(a => a.SettleAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var message = Inbox(Constants.BalanceUpdated, "txn-x", accountNumber: "NL91ABNA0417164300",
            payload: Serialize(new BalanceUpdatedEvent("txn-x", "NL91ABNA0417164300", -1m, 1m, "EUR")));
        var handler = CreateHandler(accounts: accounts, inbox: inbox);

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        inbox.Verify(r => r.MarkAsCompletedAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failing_completion_restores_the_message_status_so_the_kernel_can_record_the_retry()
    {
        // MarkAsCompletedAsync stamps the in-memory row before its save; if the
        // save (or the commit) then fails, a message still saying Completed
        // would make the kernel's retry transition a no-op (AlreadyTerminal)
        // while the database row stays Processing.
        var inbox = TransactionalInbox();
        inbox
            .Setup(r => r.MarkAsCompletedAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((m, _) =>
            {
                m.Status = MessageConstants.Status.Completed;
                m.ProcessedAt = Now.UtcDateTime;
            })
            .ThrowsAsync(new InvalidOperationException("boom"));
        var message = Inbox(Constants.TransactionCompleted, "txn-rc", payload: Serialize(new TransactionCompletedEvent("txn-rc", "Completed", Now)));
        message.Status = MessageConstants.Status.Processing;
        message.ProcessedAt = null;
        var handler = CreateHandler(inbox: inbox);

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        message.Status.Should().Be(MessageConstants.Status.Processing);
        message.ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_completion_cancelled_mid_flight_restores_the_message_status()
    {
        using var cts = new CancellationTokenSource();
        var inbox = TransactionalInbox();
        inbox
            .Setup(r => r.MarkAsCompletedAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns<InboxMessage, CancellationToken>((m, token) =>
            {
                m.Status = MessageConstants.Status.Completed;
                m.ProcessedAt = Now.UtcDateTime;
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(MessageTransitionOutcome.Applied);
            });
        var message = Inbox(Constants.TransactionCompleted, "txn-cc", payload: Serialize(new TransactionCompletedEvent("txn-cc", "Completed", Now)));
        message.Status = MessageConstants.Status.Processing;
        message.ProcessedAt = null;
        var handler = CreateHandler(inbox: inbox);

        var act = () => handler.HandleAsync(message, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        message.Status.Should().Be(MessageConstants.Status.Processing);
        message.ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_retried_transaction_attempt_starts_from_the_original_message_status()
    {
        // Npgsql retry-on-failure re-runs the transaction delegate; an attempt
        // that inherited the previous attempt's in-memory Completed would make
        // MarkAsCompletedAsync answer AlreadyTerminal and commit without
        // completing the row.
        var inbox = new Mock<IInboxMessageRepository>();
        inbox
            .Setup(r => r.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<Task>, CancellationToken>(async (operation, _) =>
            {
                await operation();
                await operation();
            });
        var statusesSeen = new List<string>();
        inbox
            .Setup(r => r.MarkAsCompletedAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((m, _) =>
            {
                statusesSeen.Add(m.Status);
                m.Status = MessageConstants.Status.Completed;
                m.ProcessedAt = Now.UtcDateTime;
            })
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var message = Inbox(Constants.TransactionCompleted, "txn-rt", payload: Serialize(new TransactionCompletedEvent("txn-rt", "Completed", Now)));
        message.Status = MessageConstants.Status.Processing;
        var handler = CreateHandler(inbox: inbox);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        statusesSeen.Should().Equal(MessageConstants.Status.Processing, MessageConstants.Status.Processing);
    }

    [Fact]
    public async Task Balance_update_tags_the_span_with_the_projection_state()
    {
        using var observedActivity = StartListenedActivity();
        var accounts = new Mock<IAccountProjectionStore>();
        var outbox = new Mock<IOutboxRepository>();
        outbox.Setup(r => r.FindByIdempotencyKeyAsync("txn-t", It.IsAny<CancellationToken>())).ReturnsAsync((OutboxMessage?)null);
        var message = Inbox(Constants.BalanceUpdated, "txn-t", accountNumber: "NL91ABNA0417164300",
            payload: Serialize(new BalanceUpdatedEvent("txn-t", "NL91ABNA0417164300", -1m, 99m, "EUR")));
        var handler = CreateHandler(outbox: outbox, accounts: accounts);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        observedActivity.Activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("account.settled_balance", 99m));
        observedActivity.Activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("account.released", 0m));
    }

    private static TransactionEventHandler CreateHandler(
        ILogger<TransactionEventHandler>? logger = null,
        Mock<IOutboxRepository>? outbox = null,
        Mock<IAccountProjectionStore>? accounts = null,
        Mock<IInboxMessageRepository>? inbox = null)
    {
        inbox ??= TransactionalInbox();
        return new TransactionEventHandler(
            logger ?? new CapturingLogger(),
            (outbox ?? new Mock<IOutboxRepository>()).Object,
            inbox.Object,
            (accounts ?? new Mock<IAccountProjectionStore>()).Object);
    }

    /// <summary>A repository whose transaction just runs the delegate and whose completion succeeds.</summary>
    private static Mock<IInboxMessageRepository> TransactionalInbox()
    {
        var inbox = new Mock<IInboxMessageRepository>();
        inbox
            .Setup(r => r.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<Task>, CancellationToken>((operation, _) => operation());
        inbox
            .Setup(r => r.MarkAsCompletedAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        return inbox;
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
