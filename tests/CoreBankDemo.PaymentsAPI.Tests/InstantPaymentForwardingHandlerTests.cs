using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// Covers <see cref="InstantPaymentForwardingHandler"/> against every
/// relevant row of the spec's I/O &amp; Edge-Case Matrix (spec:
/// add-instant-payment-rail) using Moq fakes for
/// <see cref="IOutboxMessageStore{TMessage}"/> and
/// <see cref="ICoreBankTransactionForwarder"/> -- no HTTP, no database.
/// </summary>
public class InstantPaymentForwardingHandlerTests
{
    private static readonly PaymentSnapshot Payment = new(
        Guid.NewGuid(),
        "instant-key",
        "instant-key",
        "NL91ABNA0417164300",
        "NL20INGB0001234567",
        50m,
        "EUR",
        PartitionId: 1,
        Status: MessageConstants.Status.Pending,
        CreatedAt: new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc),
        TraceParent: null,
        TraceState: null);

    private readonly Mock<IOutboxMessageStore<OutboxMessage>> _store = new(MockBehavior.Strict);
    private readonly Mock<ICoreBankTransactionForwarder> _forwarder = new(MockBehavior.Strict);
    private readonly TestLockService _lock = new();
    private readonly BusinessMetrics _businessMetrics = new();

    private static OutboxMessage ClaimedMessage() => new()
    {
        Id = Payment.Id,
        IdempotencyKey = Payment.IdempotencyKey,
        TransactionId = Payment.TransactionId,
        FromAccount = Payment.FromAccount,
        ToAccount = Payment.ToAccount,
        Amount = Payment.Amount,
        Currency = Payment.Currency,
        PartitionId = Payment.PartitionId,
        Status = MessageConstants.Status.Processing,
        CreatedAt = Payment.CreatedAt
    };

    private static TransactionSubmission CancelledSubmission(DateTimeOffset? at = null) =>
        new(Payment.TransactionId, MessageConstants.Status.Cancelled, at ?? DateTimeOffset.UtcNow);

    /// <summary>The claim-by-id + cancel pair every locally-cancelled path ends with.</summary>
    private OutboxMessage SetUpLocalCancel()
    {
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdAsync(Payment.Id, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _store.Setup(s => s.MarkAsCancelledAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        return claimed;
    }

    private InstantPaymentForwardingHandler CreateHandler(
        InstantRailOptions? options = null,
        BusinessMetrics? businessMetrics = null,
        TimeProvider? timeProvider = null) =>
        new(
            _store.Object,
            _forwarder.Object,
            _lock,
            Options.Create(options ?? new InstantRailOptions()),
            Options.Create(new OutboxProcessingOptions()),
            timeProvider ?? TimeProvider.System,
            NullLogger<InstantPaymentForwardingHandler>.Instance,
            businessMetrics ?? _businessMetrics);

    [Fact]
    public async Task ForwardAsync_rejects_a_null_payment()
    {
        var handler = CreateHandler();

        var act = () => handler.ForwardAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ForwardAsync_defers_without_claiming_when_the_rail_is_disabled()
    {
        var handler = CreateHandler(new InstantRailOptions { Enabled = false });

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
        _store.VerifyNoOtherCalls();
        _forwarder.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ForwardAsync_defers_without_claiming_when_the_lock_is_busy_and_the_processor_has_taken_the_row()
    {
        // The lock holder was the background processor, and it claimed this
        // row: nothing left to wait for -- the row is being delivered there.
        _lock.Acquired = false;
        _store.Setup(s => s.GetStatusAsync(Payment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageConstants.Status.Processing);
        var handler = CreateHandler();

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
        _lock.LockNames.Should().ContainSingle().Which.Should().Be("payments-outbox-partition-1");
        _store.Verify(s => s.GetStatusAsync(Payment.Id, It.IsAny<CancellationToken>()), Times.Once);
        _store.VerifyNoOtherCalls();
        _forwarder.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ForwardAsync_defers_when_the_row_was_claimed_by_the_processor_before_its_turn()
    {
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OutboxMessage?)null);
        _store.Setup(s => s.GetStatusAsync(Payment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageConstants.Status.Processing);
        var handler = CreateHandler();

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
        _forwarder.VerifyNoOtherCalls();
    }

    // ---- ADR-018 priority addendum: wait within budget instead of giving up ----

    [Fact]
    public async Task ForwardAsync_waits_for_its_turn_and_then_settles_inline()
    {
        // First look: an earlier instant row in the partition is still ahead.
        // Second look, 25 ms later: it has settled and this row is first.
        var claimed = ClaimedMessage();
        var looks = 0;
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++looks == 1 ? null : claimed);
        _store.Setup(s => s.GetStatusAsync(Payment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageConstants.Status.Pending);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionSubmission(Payment.TransactionId, MessageConstants.Status.Completed, DateTimeOffset.UtcNow));
        _store.Setup(s => s.MarkAsCompletedAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var handler = CreateHandler();

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Completed);
        looks.Should().Be(2);
        _lock.LockNames.Should().HaveCount(2);
    }

    [Fact]
    public async Task ForwardAsync_keeps_waiting_for_a_busy_lock_until_the_budget_is_spent()
    {
        _lock.Acquired = false;
        _store.Setup(s => s.GetStatusAsync(Payment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageConstants.Status.Pending);

        // The budget is spent on a clock this test owns, advanced 50 ms per lock attempt, so the
        // number of retries is a property of the budget rather than of how busy the machine is.
        // On the real clock this assertion was "more than one attempt" and still failed roughly
        // one run in five under a full parallel suite: a single attempt plus scheduling delay can
        // consume the whole 120 ms, leaving exactly one.
        var clock = new BudgetClock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        _lock.OnAttempt = () => clock.Advance(TimeSpan.FromMilliseconds(50));
        var claimed = SetUpLocalCancel();
        var handler = CreateHandler(
            new InstantRailOptions
            {
                BudgetMilliseconds = 120, AttemptTimeoutMilliseconds = 30, MaxAttempts = 1, CancelTimeoutMilliseconds = 20,
            },
            timeProvider: clock);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        // spec: instant-rail-timeout-cancel -- the forward phase is Budget - CancelTimeout
        // (120 - 20 = 100 ms), spent 50 ms at a time: attempts at +0 and +50 have forward
        // budget left, and the third check finds the forward deadline reached at +100. The
        // command never left PaymentsAPI, so the row is cancelled locally and the answer is
        // Cancelled (504), never the old "honest unknown" 202. Asserting the exact count is what
        // makes this a test of the budget arithmetic rather than of the scheduler.
        result.Outcome.Should().Be(InstantDeliveryOutcome.Cancelled);
        _lock.LockNames.Should().HaveCount(2, "the lock is retried until the forward phase runs out");
        _store.Verify(s => s.MarkAsCancelledAsync(claimed, InstantPaymentForwardingHandler.LocalCancelReason, It.IsAny<CancellationToken>()), Times.Once);
        _forwarder.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ForwardAsync_defers_when_the_local_cancel_claim_is_lost_to_the_processor()
    {
        // Budget spent waiting, but by the time this request tries to claim
        // the row by id the background processor has it: it may still be
        // delivered, so 504 would be a lie -- honest 202.
        _lock.Acquired = false;
        _store.Setup(s => s.GetStatusAsync(Payment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageConstants.Status.Pending);
        _store.Setup(s => s.TryClaimByIdAsync(Payment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OutboxMessage?)null);
        var clock = new BudgetClock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        _lock.OnAttempt = () => clock.Advance(TimeSpan.FromMilliseconds(50));
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var handler = CreateHandler(
            new InstantRailOptions { BudgetMilliseconds = 120, AttemptTimeoutMilliseconds = 30, MaxAttempts = 1, CancelTimeoutMilliseconds = 20 },
            businessMetrics,
            clock);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
        _store.Verify(s => s.MarkAsCancelledAsync(It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.PaymentInstantDurationInstrumentName)
            .Which.Tags["outcome"].Should().Be("deferred");
    }

    [Fact]
    public async Task ForwardAsync_local_cancel_persists_a_cancelled_payload_and_records_the_cancelled_metric()
    {
        _lock.Acquired = false;
        _store.Setup(s => s.GetStatusAsync(Payment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageConstants.Status.Pending);
        var claimed = SetUpLocalCancel();
        var clock = new BudgetClock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        _lock.OnAttempt = () => clock.Advance(TimeSpan.FromMilliseconds(200));
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var handler = CreateHandler(
            new InstantRailOptions { BudgetMilliseconds = 120, AttemptTimeoutMilliseconds = 30, MaxAttempts = 1, CancelTimeoutMilliseconds = 20 },
            businessMetrics,
            clock);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Cancelled);
        result.ProcessedAt.Should().Be(clock.GetUtcNow(), "the answer carries the cancellation timestamp");
        var payload = JsonSerializer.Deserialize<TransactionSubmission>(claimed.ResponsePayload!);
        payload.Should().Be(new TransactionSubmission(Payment.TransactionId, MessageConstants.Status.Cancelled, clock.GetUtcNow()));
        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.PaymentInstantDurationInstrumentName)
            .Which.Tags["outcome"].Should().Be("cancelled");
    }

    [Theory]
    [InlineData(MessageTransitionOutcome.AlreadyTerminal)]
    [InlineData(MessageTransitionOutcome.Conflicted)]
    public async Task ForwardAsync_defers_when_the_local_cancel_is_not_applied(MessageTransitionOutcome transition)
    {
        _lock.Acquired = false;
        _store.Setup(s => s.GetStatusAsync(Payment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageConstants.Status.Pending);
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdAsync(Payment.Id, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _store.Setup(s => s.MarkAsCancelledAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(transition);
        var clock = new BudgetClock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        _lock.OnAttempt = () => clock.Advance(TimeSpan.FromMilliseconds(200));
        var handler = CreateHandler(
            new InstantRailOptions { BudgetMilliseconds = 120, AttemptTimeoutMilliseconds = 30, MaxAttempts = 1, CancelTimeoutMilliseconds = 20 },
            timeProvider: clock);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred, "only an applied cancel is a provably dead row");
    }

    [Fact]
    public async Task ForwardAsync_defers_when_persisting_the_local_cancel_throws()
    {
        _lock.Acquired = false;
        _store.Setup(s => s.GetStatusAsync(Payment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageConstants.Status.Pending);
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdAsync(Payment.Id, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _store.Setup(s => s.MarkAsCancelledAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db unavailable"));
        var clock = new BudgetClock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        _lock.OnAttempt = () => clock.Advance(TimeSpan.FromMilliseconds(200));
        var handler = CreateHandler(
            new InstantRailOptions { BudgetMilliseconds = 120, AttemptTimeoutMilliseconds = 30, MaxAttempts = 1, CancelTimeoutMilliseconds = 20 },
            timeProvider: clock);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
    }

    [Fact]
    public async Task ForwardAsync_defers_when_the_local_cancel_claim_itself_throws()
    {
        _lock.Acquired = false;
        _store.Setup(s => s.GetStatusAsync(Payment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageConstants.Status.Pending);
        _store.Setup(s => s.TryClaimByIdAsync(Payment.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db unavailable"));
        var clock = new BudgetClock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        _lock.OnAttempt = () => clock.Advance(TimeSpan.FromMilliseconds(200));
        var handler = CreateHandler(
            new InstantRailOptions { BudgetMilliseconds = 120, AttemptTimeoutMilliseconds = 30, MaxAttempts = 1, CancelTimeoutMilliseconds = 20 },
            timeProvider: clock);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
    }

    [Fact]
    public async Task ForwardAsync_returns_the_forwarded_outcome_when_the_lock_backend_throws_after_the_workload_ran()
    {
        // Review finding: a release/renew failure AFTER the delegate ran must
        // not discard a committed outcome and cancel locally -- CoreBank may
        // have executed the command; a 504 here would be a lie.
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        var processedAt = new DateTimeOffset(2026, 9, 8, 12, 0, 2, TimeSpan.Zero);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionSubmission(Payment.TransactionId, MessageConstants.Status.Completed, processedAt));
        _store.Setup(s => s.MarkAsCompletedAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        _lock.ThrowAfterWorkload = new InvalidOperationException("redis lease release failed");
        var handler = CreateHandler();

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Completed);
        result.ProcessedAt.Should().Be(processedAt);
        _store.Verify(s => s.TryClaimByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsCancelledAsync(It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ForwardAsync_cancels_locally_when_the_status_read_fails_while_waiting()
    {
        _lock.Acquired = false;
        _store.Setup(s => s.GetStatusAsync(Payment.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db unavailable"));
        var claimed = SetUpLocalCancel();
        var handler = CreateHandler();

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Cancelled);
        _store.Verify(s => s.MarkAsCancelledAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _forwarder.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ForwardAsync_completes_on_a_committed_business_success()
    {
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        var processedAt = new DateTimeOffset(2026, 9, 2, 12, 0, 3, TimeSpan.Zero);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionSubmission(Payment.TransactionId, MessageConstants.Status.Completed, processedAt));
        _store.Setup(s => s.MarkAsCompletedAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var handler = CreateHandler(businessMetrics: businessMetrics);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Completed);
        result.ProcessedAt.Should().Be(processedAt);
        _store.Verify(s => s.MarkAsCompletedAsync(claimed, It.IsAny<CancellationToken>()), Times.Once);
        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.PaymentInstantDurationInstrumentName)
            .Which.Tags["outcome"].Should().Be("settled");
    }

    [Fact]
    public async Task ForwardAsync_reports_a_committed_business_rejection_but_still_completes_the_row()
    {
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        var processedAt = DateTimeOffset.UtcNow;
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionSubmission(Payment.TransactionId, MessageConstants.Status.Failed, processedAt));
        _store.Setup(s => s.MarkAsCompletedAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var handler = CreateHandler(businessMetrics: businessMetrics);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Rejected);
        _store.Verify(s => s.MarkAsCompletedAsync(claimed, It.IsAny<CancellationToken>()), Times.Once);
        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.PaymentInstantDurationInstrumentName)
            .Which.Tags["outcome"].Should().Be("rejected");
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Processing")]
    public async Task ForwardAsync_defers_when_CoreBank_accepted_without_committing_an_outcome(string coreBankStatus)
    {
        // CoreBankAPI answers 2xx with a non-terminal status whenever its own
        // inline execution could not run (the inbox row was not the oldest
        // claimable one in its partition, the partition lock was unavailable,
        // or execution threw but left the row retryable). That is an accepted
        // command, not a rejected payment: mapping every non-Completed status
        // to Rejected reported it to the operator as a business rejection.
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionSubmission(Payment.TransactionId, coreBankStatus, DateTimeOffset.UtcNow));
        _store.Setup(s => s.MarkAsCompletedAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var handler = CreateHandler(businessMetrics: businessMetrics);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.PaymentInstantDurationInstrumentName)
            .Which.Tags["outcome"].Should().Be("deferred");
    }

    [Fact]
    public async Task ForwardAsync_retries_a_transport_failure_within_budget_and_then_succeeds()
    {
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        var callCount = 0;
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromException<TransactionSubmission>(new InvalidOperationException("transport failure"))
                    : Task.FromResult(new TransactionSubmission(Payment.TransactionId, MessageConstants.Status.Completed, DateTimeOffset.UtcNow));
            });
        _store.Setup(s => s.MarkAsCompletedAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var handler = CreateHandler(new InstantRailOptions
        {
            BudgetMilliseconds = 9000,
            AttemptTimeoutMilliseconds = 2500,
            MaxAttempts = 2
        });

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Completed);
        callCount.Should().Be(2);
        _store.Verify(s => s.MarkAsFailedWithRetryAsync(
            It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Completion_persistence_failure_after_successful_delivery_is_not_misclassified_as_a_delivery_failure()
    {
        // Review loop 2: mirrors OutboxProcessorBaseTests's own test for the
        // exact same defect class. forwarder.ForwardAsync succeeding and then
        // MarkAsCompletedAsync throwing (e.g. a DbUpdateConcurrencyException)
        // must NOT be reported as a delivery failure: it must not re-invoke
        // forwarder.ForwardAsync (an unverified extra resubmission) and must
        // not route through MarkAsFailedWithRetryAsync (which would flip an
        // already-committed payment back to Pending and report 202 for a
        // payment that actually succeeded). The caller still receives the
        // truthful committed outcome.
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        var processedAt = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionSubmission(Payment.TransactionId, MessageConstants.Status.Completed, processedAt));
        _store.Setup(s => s.MarkAsCompletedAsync(claimed, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("concurrency conflict persisting completion"));
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var handler = CreateHandler(businessMetrics: businessMetrics);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Completed,
            "the caller must still receive the truthful outcome CoreBank already committed");
        result.ProcessedAt.Should().Be(processedAt);
        _forwarder.Verify(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()), Times.Once,
            "delivery itself succeeded and must not be retried/re-invoked for a completion-persistence failure");
        _store.Verify(s => s.MarkAsFailedWithRetryAsync(
                It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "a bookkeeping failure after successful delivery must never burn a retry or flip the message back to Pending");
        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.PaymentInstantDurationInstrumentName)
            .Which.Tags["outcome"].Should().Be("settled");
    }

    // ---- spec: instant-rail-timeout-cancel -- attempts exhausted, still under the lock ----

    [Fact]
    public async Task ForwardAsync_cancels_through_CoreBank_when_every_attempt_fails_and_CoreBank_never_stored_it()
    {
        // Matrix: "Attempts exhausted, CoreBank never stored it" -- CoreBank
        // stores a tombstone and answers 200/Cancelled; the outbox row is
        // cancelled under the lock and the caller gets 504/Cancelled.
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport failure"));
        var cancelledAt = new DateTimeOffset(2026, 9, 8, 12, 0, 7, TimeSpan.Zero);
        _forwarder.Setup(f => f.CancelAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CancelledSubmission(cancelledAt));
        _store.Setup(s => s.MarkAsCancelledAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var handler = CreateHandler(
            new InstantRailOptions { BudgetMilliseconds = 9000, AttemptTimeoutMilliseconds = 2500, MaxAttempts = 2 },
            businessMetrics);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Cancelled);
        result.ProcessedAt.Should().Be(cancelledAt);
        _forwarder.Verify(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()), Times.Exactly(2));
        _forwarder.Verify(f => f.CancelAsync(claimed, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsCancelledAsync(claimed, InstantPaymentForwardingHandler.CoreBankCancelReason, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsFailedWithRetryAsync(It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsCompletedAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _lock.LockNames.Should().ContainSingle("the cancel happens inside the one lock acquisition, never after releasing it");
        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.PaymentInstantDurationInstrumentName)
            .Which.Tags["outcome"].Should().Be("cancelled");
    }

    [Theory]
    [InlineData(MessageConstants.Status.Completed, InstantDeliveryOutcome.Completed, "settled")]
    [InlineData(MessageConstants.Status.Failed, InstantDeliveryOutcome.Rejected, "rejected")]
    public async Task ForwardAsync_reports_the_committed_outcome_when_CoreBank_had_already_executed_before_the_cancel(
        string coreBankStatus, InstantDeliveryOutcome expected, string expectedMetric)
    {
        // Matrix: "Attempts exhausted, CoreBank already executed" -- the
        // cancel answers 200 with the cached TransactionResponse; the outbox
        // row is completed with that payload and the caller gets the 200.
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport failure"));
        var processedAt = new DateTimeOffset(2026, 9, 8, 12, 0, 5, TimeSpan.Zero);
        _forwarder.Setup(f => f.CancelAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionSubmission(Payment.TransactionId, coreBankStatus, processedAt));
        _store.Setup(s => s.MarkAsCompletedAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var handler = CreateHandler(new InstantRailOptions { MaxAttempts = 1 }, businessMetrics);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(expected);
        result.ProcessedAt.Should().Be(processedAt);
        _store.Verify(s => s.MarkAsCompletedAsync(claimed, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsCancelledAsync(It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsFailedWithRetryAsync(It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.PaymentInstantDurationInstrumentName)
            .Which.Tags["outcome"].Should().Be(expectedMetric);
    }

    [Fact]
    public async Task ForwardAsync_releases_the_claim_and_defers_when_the_cancel_establishes_nothing()
    {
        // Matrix: "Cancel itself fails" -- 409 (Processing/Failed at
        // CoreBank) or a transport error: the forwarder reports null, the
        // claim goes back to Pending (unchanged path) and the caller gets
        // the residual honest 202.
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport failure"));
        _forwarder.Setup(f => f.CancelAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TransactionSubmission?)null);
        _store.Setup(s => s.MarkAsFailedWithRetryAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var handler = CreateHandler(
            new InstantRailOptions { BudgetMilliseconds = 9000, AttemptTimeoutMilliseconds = 2500, MaxAttempts = 2 },
            businessMetrics);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
        _forwarder.Verify(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()), Times.Exactly(2));
        _store.Verify(s => s.MarkAsFailedWithRetryAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsCancelledAsync(It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsCompletedAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.PaymentInstantDurationInstrumentName)
            .Which.Tags["outcome"].Should().Be("deferred");
    }

    [Fact]
    public async Task ForwardAsync_releases_the_claim_and_defers_when_the_cancel_throws()
    {
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport failure"));
        _forwarder.Setup(f => f.CancelAsync(claimed, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cancel transport failure"));
        _store.Setup(s => s.MarkAsFailedWithRetryAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var handler = CreateHandler(new InstantRailOptions { MaxAttempts = 1 });

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
        _store.Verify(s => s.MarkAsFailedWithRetryAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ForwardAsync_bounds_the_cancel_by_the_cancel_allowance_and_defers_when_it_times_out()
    {
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport failure"));
        _forwarder.Setup(f => f.CancelAsync(claimed, It.IsAny<CancellationToken>()))
            .Returns<OutboxMessage, CancellationToken>(async (_, ct) =>
            {
                // Never answers on its own: only the cancel allowance's
                // linked token ends it.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return null;
            });
        _store.Setup(s => s.MarkAsFailedWithRetryAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var handler = CreateHandler(new InstantRailOptions
        {
            BudgetMilliseconds = 9000, AttemptTimeoutMilliseconds = 2500, MaxAttempts = 1, CancelTimeoutMilliseconds = 50,
        });

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
        _store.Verify(s => s.MarkAsFailedWithRetryAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsCancelledAsync(It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(MessageTransitionOutcome.AlreadyTerminal)]
    [InlineData(MessageTransitionOutcome.Conflicted)]
    public async Task ForwardAsync_defers_when_CoreBank_cancelled_but_the_outbox_cancel_was_not_applied(MessageTransitionOutcome transition)
    {
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport failure"));
        _forwarder.Setup(f => f.CancelAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CancelledSubmission());
        _store.Setup(s => s.MarkAsCancelledAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(transition);
        var handler = CreateHandler(new InstantRailOptions { MaxAttempts = 1 });

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
    }

    [Fact]
    public async Task ForwardAsync_cancels_the_row_when_a_forward_attempt_replays_a_CoreBank_cancellation()
    {
        // A tombstone from an earlier cancel this side never heard about is
        // a terminal nothing-executed answer, not a delivery to complete.
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        var cancelledAt = new DateTimeOffset(2026, 9, 8, 12, 0, 1, TimeSpan.Zero);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CancelledSubmission(cancelledAt));
        _store.Setup(s => s.MarkAsCancelledAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var handler = CreateHandler();

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Cancelled);
        result.ProcessedAt.Should().Be(cancelledAt);
        _store.Verify(s => s.MarkAsCompletedAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _forwarder.Verify(f => f.CancelAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ForwardAsync_stops_immediately_once_the_budget_is_exhausted_mid_retry()
    {
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport failure"));
        _store.Setup(s => s.MarkAsFailedWithRetryAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        // Budget only covers a single attempt window: the clock jumps past
        // the deadline between the first failed attempt and the loop's next
        // remaining-budget check, so a second attempt must never start.
        // Reads, in order: startedAt, the outer wait-loop's budget check, the
        // first attempt's budget check, then the post-failure check.
        var timeProvider = new SequencedTimeProvider(
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 2, 12, 0, 10, TimeSpan.Zero));
        var handler = new InstantPaymentForwardingHandler(
            _store.Object,
            _forwarder.Object,
            _lock,
            Options.Create(new InstantRailOptions { BudgetMilliseconds = 9000, AttemptTimeoutMilliseconds = 2500, MaxAttempts = 5 }),
            Options.Create(new OutboxProcessingOptions()),
            timeProvider,
            NullLogger<InstantPaymentForwardingHandler>.Instance,
            _businessMetrics);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        // The whole budget is gone, so there is no allowance left for a cancel
        // either: the claim is released and the honest 202 remains.
        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
        _forwarder.Verify(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()), Times.Once);
        _forwarder.Verify(f => f.CancelAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsFailedWithRetryAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ForwardAsync_propagates_caller_cancellation_without_releasing_the_claim()
    {
        using var cancellation = new CancellationTokenSource();
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await cancellation.CancelAsync();
                throw new OperationCanceledException(cancellation.Token);
            });
        var handler = CreateHandler();

        var act = () => handler.ForwardAsync(Payment, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _store.Verify(s => s.MarkAsFailedWithRetryAsync(
            It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsCompletedAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ForwardAsync_retries_within_budget_after_a_per_attempt_timeout()
    {
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        var callCount = 0;
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .Returns<OutboxMessage, bool, CancellationToken>(async (_, _, ct) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Never completes on its own within the per-attempt
                    // timeout -- the linked token cancels it.
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }

                return new TransactionSubmission(Payment.TransactionId, MessageConstants.Status.Completed, DateTimeOffset.UtcNow);
            });
        _store.Setup(s => s.MarkAsCompletedAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var handler = CreateHandler(new InstantRailOptions
        {
            BudgetMilliseconds = 9000,
            AttemptTimeoutMilliseconds = 50,
            MaxAttempts = 2
        });

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Completed);
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task ForwardAsync_defers_when_releasing_the_claim_itself_fails()
    {
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport failure"));
        _forwarder.Setup(f => f.CancelAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TransactionSubmission?)null);
        _store.Setup(s => s.MarkAsFailedWithRetryAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db unavailable"));
        var handler = CreateHandler(new InstantRailOptions { MaxAttempts = 1 });

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
    }

    [Fact]
    public async Task ForwardAsync_returns_the_true_result_when_lock_ownership_is_lost_after_the_workload_completes()
    {
        // Patch 1 regression test: ExecuteWithLockAsync returns false both
        // when the lock was never acquired (callback never ran) AND when the
        // callback ran to completion but ownership was lost mid-flight. The
        // second case must still return the real, committed result -- not
        // silently downgrade a completed forward to Deferred.
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        var processedAt = new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionSubmission(Payment.TransactionId, MessageConstants.Status.Completed, processedAt));
        _store.Setup(s => s.MarkAsCompletedAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        _lock.LoseOwnershipAfterWorkload = true;
        var handler = CreateHandler();

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Completed,
            "the forward genuinely completed even though lock ownership was reported lost afterward");
        result.ProcessedAt.Should().Be(processedAt);
    }

    [Fact]
    public async Task ForwardAsync_cancels_locally_when_the_lock_backend_throws()
    {
        // Patch 2 regression test, re-based on spec: instant-rail-timeout-
        // cancel: an exception from ExecuteWithLockAsync itself (e.g. a Redis
        // connection failure) must degrade gracefully rather than propagate
        // out of ForwardAsync -- and since the command never left
        // PaymentsAPI, the row is cancelled locally (504), never forwarded.
        _lock.ThrowException = new InvalidOperationException("redis connection failure");
        var claimed = SetUpLocalCancel();
        var handler = CreateHandler();

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Cancelled);
        _store.Verify(s => s.TryClaimByIdAsync(Payment.Id, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsCancelledAsync(claimed, InstantPaymentForwardingHandler.LocalCancelReason, It.IsAny<CancellationToken>()), Times.Once);
        _store.VerifyNoOtherCalls();
        _forwarder.VerifyNoOtherCalls();
    }

    private sealed class SequencedTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int _index;

        public override DateTimeOffset GetUtcNow()
        {
            var value = values[Math.Min(_index, values.Length - 1)];
            _index++;
            return value;
        }
    }

    /// <summary>
    /// A clock the test drives, for the budgeted lock-wait loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TimeProvider.System"/> cannot express this test: the loop retries until wall
    /// time passes the budget, so the number of attempts is decided by how fast the machine is
    /// rather than by the arithmetic under test.
    /// </para>
    /// <para>
    /// A stock fake clock cannot express it either, and would hang. The loop's backoff is
    /// <c>Task.Delay(ClaimRetryDelay, timeProvider, ct)</c>, which builds its timer from this
    /// provider -- so a clock that only moves when the test says so would leave that delay
    /// pending for ever, with nothing left running to advance it. Timers here therefore fire as
    /// soon as they are created: the backoff is real control flow the loop must survive, but its
    /// duration is not what the test is about. Only <see cref="GetUtcNow"/> carries the passage
    /// of time, and only <see cref="Advance"/> moves it.
    /// </para>
    /// </remarks>
    private sealed class BudgetClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new ImmediateTimer(callback, state);

        private sealed class ImmediateTimer : ITimer
        {
            public ImmediateTimer(TimerCallback callback, object? state) =>
                // Queued rather than invoked inline: Task.Delay is still wiring up its own state
                // when CreateTimer returns, and completing it underneath that is a race.
                ThreadPool.QueueUserWorkItem(_ => callback(state));

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TestLockService : IDistributedLockService
    {
        public bool Acquired { get; set; } = true;

        /// <summary>
        /// When set, the workload still runs to completion but this reports
        /// <see langword="false"/> anyway -- reproducing
        /// <see cref="RedisDistributedLockService"/>'s documented "lock
        /// ownership was lost during the workload; not reporting success"
        /// case (as distinct from <see cref="Acquired"/> = <see langword="false"/>,
        /// where the workload never runs at all).
        /// </summary>
        public bool LoseOwnershipAfterWorkload { get; set; }

        /// <summary>When set, the call throws instead of returning -- simulating a lock backend failure (e.g. Redis connection failure).</summary>
        public Exception? ThrowException { get; set; }

        /// <summary>When set, the workload runs to completion and THEN the call throws -- a release/renew failure after the work happened.</summary>
        public Exception? ThrowAfterWorkload { get; set; }

        public List<string> LockNames { get; } = [];

        /// <summary>
        /// Runs on every acquisition attempt. Budget tests hang the clock off this, so time only
        /// moves when the handler actually retries, never on its own.
        /// </summary>
        public Action? OnAttempt { get; set; }

        public async Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            LockNames.Add(lockName);
            OnAttempt?.Invoke();
            if (ThrowException is not null)
            {
                throw ThrowException;
            }

            if (!Acquired)
            {
                return false;
            }

            await workload(cancellationToken);
            if (ThrowAfterWorkload is not null)
            {
                throw ThrowAfterWorkload;
            }

            return !LoseOwnershipAfterWorkload;
        }
    }
}
