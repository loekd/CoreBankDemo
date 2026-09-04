using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.PaymentsAPI.Handlers;

/// <summary>
/// Authoritative outcome of one instant-rail forward attempt.
/// <see cref="Completed"/>/<see cref="Rejected"/> mean CoreBank confirmed a
/// committed outcome (business success or business rejection respectively)
/// within budget; <see cref="Deferred"/> covers every case that falls back to
/// the unchanged background rail (budget/attempts exhausted, a transport
/// failure, the row already claimed by the processor, or the rail disabled).
/// </summary>
public enum InstantDeliveryOutcome
{
    Completed,
    Rejected,
    Deferred
}

/// <summary>Result of <see cref="IInstantPaymentForwardingHandler.ForwardAsync"/>.</summary>
public sealed record InstantForwardResult(InstantDeliveryOutcome Outcome, DateTimeOffset ProcessedAt);

/// <summary>
/// Instant-rail decorator that runs strictly after storage (spec:
/// add-instant-payment-rail; AD-2: <see cref="PaymentStorageHandler"/> stays
/// pure and untouched -- the budget loop lives only here). Claims the
/// already-persisted outbox row through the kernel's claim path
/// (<see cref="IOutboxMessageStore{TMessage}.TryClaimByIdAsync"/>) so an
/// inline attempt can never race the background <c>PaymentsOutboxProcessor</c>
/// into a double delivery, then makes a budgeted, per-attempt-timeout-bounded
/// inline forward via the same <see cref="ICoreBankTransactionForwarder"/>
/// sequence the background processor's delivery strategy uses.
/// </summary>
public interface IInstantPaymentForwardingHandler
{
    Task<InstantForwardResult> ForwardAsync(PaymentSnapshot payment, CancellationToken cancellationToken);
}

internal sealed class InstantPaymentForwardingHandler(
    IOutboxMessageStore<OutboxMessage> store,
    ICoreBankTransactionForwarder forwarder,
    IDistributedLockService lockService,
    IOptions<InstantRailOptions> options,
    IOptions<OutboxProcessingOptions> outboxOptions,
    TimeProvider timeProvider,
    ILogger<InstantPaymentForwardingHandler> logger,
    BusinessMetrics businessMetrics) : IInstantPaymentForwardingHandler
{
    /// <summary>
    /// Pause between "not yet first in dispatch order" checks while waiting
    /// within budget. Short, because the rows ahead are typically settled
    /// inline by their own requests in tens of milliseconds.
    /// </summary>
    private static readonly TimeSpan ClaimRetryDelay = TimeSpan.FromMilliseconds(25);

    public async Task<InstantForwardResult> ForwardAsync(PaymentSnapshot payment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var opts = options.Value;
        var startedAt = timeProvider.GetUtcNow();

        if (!opts.Enabled)
        {
            logger.LogInformation(
                "Instant rail disabled; payment {IdempotencyKey} deferred to background delivery", payment.IdempotencyKey);
            return Deferred(startedAt);
        }

        var deadline = startedAt + TimeSpan.FromMilliseconds(opts.BudgetMilliseconds);
        var attemptTimeout = TimeSpan.FromMilliseconds(opts.AttemptTimeoutMilliseconds);
        var lockName = $"payments-outbox-partition-{payment.PartitionId}";

        // Under load, a busy partition lock and "not first in dispatch order
        // yet" are the normal case, not the exception: every concurrent
        // instant request in the partition and the background processor's
        // 200 ms poll all contend for the same lock. Giving up at the first
        // sign of contention deferred almost every instant payment in a
        // burst. The budget exists precisely so an SCT Inst can wait a
        // bounded time instead, so this waits -- for the lock, and for its
        // turn -- until the budget runs out. It stops early the moment
        // another claimant owns the row: that row is being delivered by the
        // background processor and its outcome will arrive via the event.
        while (true)
        {
            var remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                logger.LogInformation(
                    "Instant rail: budget exhausted waiting for partition {PartitionId} to reach payment {IdempotencyKey}; deferred to background delivery",
                    payment.PartitionId,
                    payment.IdempotencyKey);
                return Deferred(startedAt);
            }

            InstantForwardResult? result = null;
            try
            {
                await lockService.ExecuteWithLockAsync(
                    lockName,
                    outboxOptions.Value.LockExpirySeconds,
                    remaining < attemptTimeout ? remaining : attemptTimeout,
                    async lockToken =>
                    {
                        result = await ForwardUnderPartitionLockAsync(
                            payment,
                            opts,
                            startedAt,
                            lockToken).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Instant rail: lock backend failed for partition {PartitionId} while forwarding payment {IdempotencyKey}; deferred to background delivery",
                    payment.PartitionId,
                    payment.IdempotencyKey);
                return Deferred(startedAt);
            }

            // ExecuteWithLockAsync returns false both when the lock was never
            // acquired (the callback never ran, so result is still null) AND
            // when the workload ran but lock ownership was lost mid-flight.
            // In the second case result is non-null -- the forward genuinely
            // happened -- and is trusted and returned; only a null result
            // (lock unavailable, or not yet first in dispatch order) waits.
            if (result is not null)
            {
                return result;
            }

            string? currentStatus;
            try
            {
                currentStatus = await store.GetStatusAsync(payment.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Instant rail: could not read payment {IdempotencyKey} while waiting for partition {PartitionId}; deferred to background delivery",
                    payment.IdempotencyKey,
                    payment.PartitionId);
                return Deferred(startedAt);
            }

            if (currentStatus != MessageConstants.Status.Pending)
            {
                logger.LogInformation(
                    "Instant rail: payment {IdempotencyKey} was claimed by the background processor while waiting for partition {PartitionId}; deferred to that delivery",
                    payment.IdempotencyKey,
                    payment.PartitionId);
                return Deferred(startedAt);
            }

            await Task.Delay(ClaimRetryDelay, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The one budgeted forward attempt under the partition lock. Returns
    /// <see langword="null"/> -- rather than a deferral -- when the row is not
    /// yet first in dispatch order, so the caller can wait and try again.
    /// </summary>
    private async Task<InstantForwardResult?> ForwardUnderPartitionLockAsync(
        PaymentSnapshot payment,
        InstantRailOptions opts,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var claimed = await store.TryClaimByIdIfOldestAsync(
            payment.Id,
            payment.PartitionId,
            cancellationToken).ConfigureAwait(false);
        if (claimed is null)
        {
            logger.LogDebug(
                "Instant rail: payment {IdempotencyKey} is not yet first in dispatch order for partition {PartitionId}",
                payment.IdempotencyKey,
                payment.PartitionId);
            return null;
        }

        var budget = TimeSpan.FromMilliseconds(opts.BudgetMilliseconds);
        var attemptTimeout = TimeSpan.FromMilliseconds(opts.AttemptTimeoutMilliseconds);
        var deadline = startedAt + budget;

        for (var attempt = 1; attempt <= opts.MaxAttempts; attempt++)
        {
            var remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var thisAttemptTimeout = remaining < attemptTimeout ? remaining : attemptTimeout;
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(thisAttemptTimeout);

            try
            {
                var submission = await forwarder
                    .ForwardAsync(claimed, executeInline: true, attemptCts.Token)
                    .ConfigureAwait(false);

                // Review loop 2: delivery succeeded -- completion-persistence
                // is now handled in its OWN try/catch, deliberately never
                // sharing this method's outer catch blocks, mirroring
                // OutboxProcessorBase.ProcessMessageAsync's fix for the exact
                // same defect class. A single shared try/catch cannot tell
                // "delivery failed" apart from "delivery succeeded but
                // MarkAsCompletedAsync then failed"; misclassifying the
                // latter as a delivery failure would re-invoke
                // forwarder.ForwardAsync below (an unverified extra
                // resubmission relying solely on CoreBank's own dedupe) and,
                // on exhaustion, call MarkAsFailedWithRetryAsync -- flipping
                // an already-committed payment back to Pending and reporting
                // 202 for a payment that actually succeeded. The caller still
                // receives the truthful outcome CoreBank already confirmed;
                // on a persistence failure the row is simply left exactly as
                // claiming left it (Processing) rather than retried or
                // failed, to be naturally reclaimed once its claim goes stale
                // -- the same recovery the "reply lost after commit" edge
                // case already relies on (the background processor's replay
                // is absorbed by CoreBank's own dedupe).
                try
                {
                    await store.MarkAsCompletedAsync(claimed, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to persist instant-rail completion for payment {IdempotencyKey} after a successful delivery; the row is left Processing for the background processor to complete once its claim goes stale",
                        payment.IdempotencyKey);
                }

                // Only a terminal CoreBank status is a committed business
                // outcome. A 2xx carrying Pending/Processing means CoreBank
                // accepted the command for its own deferred execution --
                // TransactionIntakeHandler answers 202/Pending whenever
                // inline execution could not run (the inbox row was not the
                // first in dispatch order for its partition, the partition lock
                // was unavailable, or execution threw but left the row
                // retryable). Reading "not Completed" as Rejected reported
                // that deferral to the operator as a business rejection --
                // a 200/Failed for a payment nobody had rejected.
                var outcome = submission.Status switch
                {
                    MessageConstants.Status.Completed => InstantDeliveryOutcome.Completed,
                    MessageConstants.Status.Failed => InstantDeliveryOutcome.Rejected,
                    _ => InstantDeliveryOutcome.Deferred,
                };

                if (outcome == InstantDeliveryOutcome.Deferred)
                {
                    logger.LogInformation(
                        "Instant rail: CoreBank accepted payment {IdempotencyKey} with non-committed status {Status}; reporting no committed outcome yet",
                        payment.IdempotencyKey,
                        submission.Status);
                }

                businessMetrics.RecordInstantPaymentDuration(
                    outcome switch
                    {
                        InstantDeliveryOutcome.Completed => BusinessMetrics.InstantPaymentOutcome.Settled,
                        InstantDeliveryOutcome.Rejected => BusinessMetrics.InstantPaymentOutcome.Rejected,
                        _ => BusinessMetrics.InstantPaymentOutcome.Deferred,
                    },
                    timeProvider.GetUtcNow() - startedAt);

                return new InstantForwardResult(outcome, submission.ProcessedAt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller (not the per-attempt timeout) cancelled -- no
                // measurement is recorded solely because of cancellation, and
                // the claimed row is left exactly as claiming left it
                // (Processing); it will be naturally reclaimed once its
                // claim goes stale.
                throw;
            }
            catch (OperationCanceledException)
            {
                // Per-attempt timeout: retry while budget/attempts remain.
                logger.LogInformation(
                    "Instant rail attempt {Attempt} timed out for payment {IdempotencyKey}",
                    attempt, payment.IdempotencyKey);
            }
            catch (Exception ex)
            {
                // Transport failure -- counts toward retry, never toward a
                // business outcome (AD-11); the existing retry policy is
                // "retry within budget/attempts, then release the claim".
                // Never reached for a completion-persistence failure -- that
                // is caught and handled above, inside the inner try/catch,
                // before this point.
                logger.LogWarning(
                    ex,
                    "Instant rail attempt {Attempt} failed for payment {IdempotencyKey}",
                    attempt, payment.IdempotencyKey);
            }
        }

        // Budget or attempts exhausted: release the claim back to Pending
        // through the same transport-failure transition the background
        // processor uses, so the row is picked up by the next poll tick --
        // never left claimed, never marked terminally Failed by this call
        // alone (MaxRetryCount is still enforced by the shared kernel path).
        try
        {
            await store.MarkAsFailedWithRetryAsync(
                claimed, "Instant rail budget exhausted", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to release the instant-rail claim for payment {IdempotencyKey}; row will be reclaimed once stale",
                payment.IdempotencyKey);
        }

        return Deferred(startedAt);
    }

    private InstantForwardResult Deferred(DateTimeOffset startedAt)
    {
        businessMetrics.RecordInstantPaymentDuration(
            BusinessMetrics.InstantPaymentOutcome.Deferred, timeProvider.GetUtcNow() - startedAt);
        return new InstantForwardResult(InstantDeliveryOutcome.Deferred, startedAt);
    }
}
