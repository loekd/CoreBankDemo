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

    private InstantPaymentForwardingHandler CreateHandler(
        InstantRailOptions? options = null, BusinessMetrics? businessMetrics = null) =>
        new(
            _store.Object,
            _forwarder.Object,
            _lock,
            Options.Create(options ?? new InstantRailOptions()),
            Options.Create(new OutboxProcessingOptions()),
            TimeProvider.System,
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
    public async Task ForwardAsync_defers_without_claiming_when_partition_lock_is_unavailable()
    {
        _lock.Acquired = false;
        var handler = CreateHandler();

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
        _lock.LockNames.Should().ContainSingle().Which.Should().Be("payments-outbox-partition-1");
        _store.VerifyNoOtherCalls();
        _forwarder.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ForwardAsync_defers_when_the_row_is_not_claimable()
    {
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OutboxMessage?)null);
        var handler = CreateHandler();

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
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

    [Fact]
    public async Task ForwardAsync_releases_the_claim_when_every_attempt_fails()
    {
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport failure"));
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
        _store.Verify(s => s.MarkAsCompletedAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.PaymentInstantDurationInstrumentName)
            .Which.Tags["outcome"].Should().Be("deferred");
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
        var timeProvider = new SequencedTimeProvider(
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

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
        _forwarder.Verify(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task ForwardAsync_defers_gracefully_when_the_lock_backend_throws()
    {
        // Patch 2 regression test: an exception from ExecuteWithLockAsync
        // itself (e.g. a Redis connection failure) must degrade gracefully
        // to Deferred, matching every other transport hiccup in this method,
        // rather than propagating out of ForwardAsync.
        _lock.ThrowException = new InvalidOperationException("redis connection failure");
        var handler = CreateHandler();

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred);
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

        public List<string> LockNames { get; } = [];

        public async Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            LockNames.Add(lockName);
            if (ThrowException is not null)
            {
                throw ThrowException;
            }

            if (!Acquired)
            {
                return false;
            }

            await workload(cancellationToken);
            return !LoseOwnershipAfterWorkload;
        }
    }
}
