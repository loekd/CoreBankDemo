using System.Text.Json;
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
/// within budget; <see cref="Cancelled"/> means the budget ran out and the
/// command was provably withdrawn before it executed (spec:
/// instant-rail-timeout-cancel) -- the caller may safely retry with a new
/// key; <see cref="Deferred"/> covers every case that falls back to the
/// unchanged background rail (the row already claimed by the processor, the
/// rail disabled, or the residual unknown where neither a cancel nor a
/// committed outcome could be established within budget).
/// </summary>
public enum InstantDeliveryOutcome
{
    Completed,
    Rejected,
    Deferred,
    Cancelled
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
///
/// <para>
/// spec: instant-rail-timeout-cancel. The budget is split in two: a forward
/// phase of <c>Budget - CancelTimeout</c> and a cancel allowance of
/// <c>CancelTimeout</c>. When the forward phase ends without a committed
/// outcome the row is cancelled -- locally when the command never left
/// PaymentsAPI, otherwise through CoreBank's cancel endpoint, still under the
/// partition lock the request already holds -- and only a provably dead row
/// answers <see cref="InstantDeliveryOutcome.Cancelled"/>. When neither a
/// cancel nor a committed outcome can be established the claim is released
/// and the honest <see cref="InstantDeliveryOutcome.Deferred"/> remains.
/// </para>
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

    internal const string LocalCancelReason = "Instant rail budget exhausted before the command left PaymentsAPI";
    internal const string CoreBankCancelReason = "Instant rail budget exhausted; cancelled at CoreBank before execution";

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

        // The forward phase ends a cancel allowance before the budget does,
        // so the cancel that follows never holds the request past the budget.
        var forwardDeadline = ForwardDeadline(startedAt, opts);
        var attemptTimeout = TimeSpan.FromMilliseconds(opts.AttemptTimeoutMilliseconds);
        var lockName = $"payments-outbox-partition-{payment.PartitionId}";

        // Under load, a busy partition lock and "not first in dispatch order
        // yet" are the normal case, not the exception: every concurrent
        // instant request in the partition and the background processor's
        // 200 ms poll all contend for the same lock. Giving up at the first
        // sign of contention deferred almost every instant payment in a
        // burst. The budget exists precisely so an SCT Inst can wait a
        // bounded time instead, so this waits -- for the lock, and for its
        // turn -- until the forward phase runs out. It stops early the moment
        // another claimant owns the row: that row is being delivered by the
        // background processor and its outcome will arrive via the event.
        while (true)
        {
            var remaining = forwardDeadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                logger.LogInformation(
                    "Instant rail: budget exhausted waiting for partition {PartitionId} to reach payment {IdempotencyKey}; cancelling locally",
                    payment.PartitionId,
                    payment.IdempotencyKey);
                return await CancelLocallyAsync(payment, startedAt, LocalCancelReason, cancellationToken).ConfigureAwait(false);
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
                // The lock backend can throw AFTER the workload ran (a release
                // or renew failure). The forward -- and possibly CoreBank's
                // execution -- genuinely happened then, so that result is
                // trusted and returned; cancelling locally here would answer
                // 504 for a command that may be executing. Only a failure
                // that prevented the workload from ever running (result still
                // null) means the command never left PaymentsAPI.
                if (result is not null)
                {
                    logger.LogWarning(
                        ex,
                        "Instant rail: lock backend failed for partition {PartitionId} after forwarding payment {IdempotencyKey}; returning the forwarded outcome",
                        payment.PartitionId,
                        payment.IdempotencyKey);
                    return result;
                }

                logger.LogWarning(
                    ex,
                    "Instant rail: lock backend failed for partition {PartitionId} while forwarding payment {IdempotencyKey}; cancelling locally",
                    payment.PartitionId,
                    payment.IdempotencyKey);
                return await CancelLocallyAsync(payment, startedAt, LocalCancelReason, cancellationToken).ConfigureAwait(false);
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
                    "Instant rail: could not read payment {IdempotencyKey} while waiting for partition {PartitionId}; cancelling locally",
                    payment.IdempotencyKey,
                    payment.PartitionId);
                return await CancelLocallyAsync(payment, startedAt, LocalCancelReason, cancellationToken).ConfigureAwait(false);
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

        var attemptTimeout = TimeSpan.FromMilliseconds(opts.AttemptTimeoutMilliseconds);
        var forwardDeadline = ForwardDeadline(startedAt, opts);

        for (var attempt = 1; attempt <= opts.MaxAttempts; attempt++)
        {
            var remaining = forwardDeadline - timeProvider.GetUtcNow();
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

                // CoreBank replaying a cancellation for this command (a
                // tombstone from an earlier attempt's cancel that this side
                // never heard about) is a terminal, nothing-executed answer:
                // the row must end Cancelled, never Completed.
                if (submission.Status == MessageConstants.Status.Cancelled)
                {
                    return await ConcludeCancelledAsync(payment, claimed, submission, startedAt, CoreBankCancelReason, cancellationToken)
                        .ConfigureAwait(false);
                }

                return await ConcludeDeliveredAsync(payment, claimed, submission, startedAt, cancellationToken)
                    .ConfigureAwait(false);
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
                // "retry within budget/attempts, then cancel".
                logger.LogWarning(
                    ex,
                    "Instant rail attempt {Attempt} failed for payment {IdempotencyKey}",
                    attempt, payment.IdempotencyKey);
            }
        }

        // Budget or attempts exhausted, still under the partition lock (so a
        // later row in this partition/priority cannot overtake this one while
        // its fate is decided): two-phase cancel through CoreBank.
        return await CancelThroughCoreBankAsync(payment, claimed, opts, startedAt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The forward attempts ended without a committed outcome and the command
    /// may or may not have reached CoreBank. Asks CoreBank, within the cancel
    /// allowance, and maps its answer per the spec's matrix:
    /// <c>Cancelled</c> → cancel the row and answer <c>504</c>;
    /// <c>Completed</c>/<c>Failed</c> → complete the row with that committed
    /// payload and answer <c>200</c>; anything else (409, timeout, transport
    /// error) → release the claim to <c>Pending</c> and keep the honest
    /// <c>202</c>.
    /// </summary>
    private async Task<InstantForwardResult> CancelThroughCoreBankAsync(
        PaymentSnapshot payment,
        OutboxMessage claimed,
        InstantRailOptions opts,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var budgetDeadline = startedAt + TimeSpan.FromMilliseconds(opts.BudgetMilliseconds);
        var allowance = TimeSpan.FromMilliseconds(opts.CancelTimeoutMilliseconds);
        var remaining = budgetDeadline - timeProvider.GetUtcNow();
        if (remaining < allowance)
        {
            allowance = remaining;
        }

        TransactionSubmission? answer = null;
        if (allowance > TimeSpan.Zero)
        {
            using var cancelCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancelCts.CancelAfter(allowance);
            try
            {
                answer = await forwarder.CancelAsync(claimed, cancelCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation(
                    "Instant rail: cancel for payment {IdempotencyKey} timed out after {AllowanceMilliseconds} ms; the outcome stays unknown",
                    payment.IdempotencyKey,
                    allowance.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Instant rail: cancel for payment {IdempotencyKey} failed; the outcome stays unknown",
                    payment.IdempotencyKey);
            }
        }
        else
        {
            logger.LogInformation(
                "Instant rail: no budget left to cancel payment {IdempotencyKey}; the outcome stays unknown",
                payment.IdempotencyKey);
        }

        switch (answer?.Status)
        {
            case MessageConstants.Status.Cancelled:
                return await ConcludeCancelledAsync(payment, claimed, answer, startedAt, CoreBankCancelReason, cancellationToken)
                    .ConfigureAwait(false);

            case MessageConstants.Status.Completed:
            case MessageConstants.Status.Failed:
                logger.LogInformation(
                    "Instant rail: CoreBank had already executed payment {IdempotencyKey} ({Status}) when the cancel arrived; reporting the committed outcome",
                    payment.IdempotencyKey,
                    answer.Status);
                return await ConcludeDeliveredAsync(payment, claimed, answer, startedAt, cancellationToken).ConfigureAwait(false);

            default:
                // Residual honest unknown: 409 (in flight / terminally failed
                // at CoreBank), a timed-out or failed cancel, or a 200 whose
                // status commits nothing. Release the claim back to Pending
                // through the same transport-failure transition the
                // background processor uses, so the row is picked up by the
                // next poll tick -- never left claimed, never marked
                // terminally Failed by this call alone (MaxRetryCount is
                // still enforced by the shared kernel path).
                await ReleaseClaimAsync(payment, claimed, cancellationToken).ConfigureAwait(false);
                return Deferred(startedAt);
        }
    }

    /// <summary>
    /// The never-forwarded paths (spec matrix: "Never reached CoreBank"): the
    /// command cannot exist at CoreBank, so the row is cancelled here without
    /// a network call. Claimed by id first -- the same concurrency-token
    /// transition every claimer uses -- so a row the background processor has
    /// meanwhile taken is never cancelled underneath it: a lost claim means
    /// the row is in flight elsewhere and the answer stays <c>202</c>.
    /// </summary>
    private async Task<InstantForwardResult> CancelLocallyAsync(
        PaymentSnapshot payment,
        DateTimeOffset startedAt,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var claimed = await store.TryClaimByIdAsync(payment.Id, cancellationToken).ConfigureAwait(false);
            if (claimed is null)
            {
                logger.LogInformation(
                    "Instant rail: payment {IdempotencyKey} is no longer claimable (in flight elsewhere); deferred to that delivery",
                    payment.IdempotencyKey);
                return Deferred(startedAt);
            }

            var cancelledAt = timeProvider.GetUtcNow();
            var submission = new TransactionSubmission(claimed.TransactionId, MessageConstants.Status.Cancelled, cancelledAt);
            claimed.ResponsePayload = JsonSerializer.Serialize(submission);
            return await ConcludeCancelledAsync(payment, claimed, submission, startedAt, reason, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Instant rail: could not cancel payment {IdempotencyKey} locally; deferred to background delivery",
                payment.IdempotencyKey);
            return Deferred(startedAt);
        }
    }

    /// <summary>
    /// Persists the cancellation on the claimed row (<c>Status = Cancelled</c>,
    /// <c>ProcessedAt</c>, reason; the cached payload is already on the tracked
    /// entity) and answers <see cref="InstantDeliveryOutcome.Cancelled"/>. Only
    /// an <see cref="MessageTransitionOutcome.Applied"/> transition is a
    /// provably dead row; anything else means another writer moved the row
    /// since the claim, and the honest answer is the deferral.
    /// </summary>
    private async Task<InstantForwardResult> ConcludeCancelledAsync(
        PaymentSnapshot payment,
        OutboxMessage claimed,
        TransactionSubmission submission,
        DateTimeOffset startedAt,
        string reason,
        CancellationToken cancellationToken)
    {
        MessageTransitionOutcome transition;
        try
        {
            transition = await store.MarkAsCancelledAsync(claimed, reason, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The row is left exactly as claiming left it (Processing) and
            // will be reclaimed once stale; CoreBank's own dedupe absorbs the
            // redelivery. The caller is told the honest unknown, never 504.
            logger.LogWarning(
                ex,
                "Instant rail: failed to persist the cancellation of payment {IdempotencyKey}; deferred to background delivery",
                payment.IdempotencyKey);
            return Deferred(startedAt);
        }

        if (transition != MessageTransitionOutcome.Applied)
        {
            logger.LogWarning(
                "Instant rail: cancellation of payment {IdempotencyKey} was not applied ({Transition}); deferred to background delivery",
                payment.IdempotencyKey,
                transition);
            return Deferred(startedAt);
        }

        logger.LogInformation(
            "Instant rail: payment {IdempotencyKey} cancelled ({Reason})", payment.IdempotencyKey, reason);
        businessMetrics.RecordInstantPaymentDuration(
            BusinessMetrics.InstantPaymentOutcome.Cancelled, timeProvider.GetUtcNow() - startedAt);
        return new InstantForwardResult(InstantDeliveryOutcome.Cancelled, submission.ProcessedAt);
    }

    /// <summary>
    /// CoreBank answered the delivery (or the cancel) with a committed or
    /// accepted status: completes the row and maps the status to the outcome.
    /// </summary>
    private async Task<InstantForwardResult> ConcludeDeliveredAsync(
        PaymentSnapshot payment,
        OutboxMessage claimed,
        TransactionSubmission submission,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        // Review loop 2: delivery succeeded -- completion-persistence is
        // handled in its OWN try/catch, deliberately never sharing the
        // attempt loop's catch blocks, mirroring
        // OutboxProcessorBase.ProcessMessageAsync's fix for the exact same
        // defect class. A single shared try/catch cannot tell "delivery
        // failed" apart from "delivery succeeded but MarkAsCompletedAsync
        // then failed"; misclassifying the latter as a delivery failure
        // would re-invoke forwarder.ForwardAsync (an unverified extra
        // resubmission relying solely on CoreBank's own dedupe) and, on
        // exhaustion, release or cancel an already-committed payment. The
        // caller still receives the truthful outcome CoreBank already
        // confirmed; on a persistence failure the row is simply left exactly
        // as claiming left it (Processing) rather than retried or failed, to
        // be naturally reclaimed once its claim goes stale -- the same
        // recovery the "reply lost after commit" edge case already relies on
        // (the background processor's replay is absorbed by CoreBank's own
        // dedupe).
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

        // Only a terminal CoreBank status is a committed business outcome. A
        // 2xx carrying Pending/Processing means CoreBank accepted the command
        // for its own deferred execution -- TransactionIntakeHandler answers
        // 202/Pending whenever inline execution could not run (the inbox row
        // was not the first in dispatch order for its partition, the
        // partition lock was unavailable, or execution threw but left the
        // row retryable). Reading "not Completed" as Rejected reported that
        // deferral to the operator as a business rejection -- a 200/Failed
        // for a payment nobody had rejected.
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

    private async Task ReleaseClaimAsync(PaymentSnapshot payment, OutboxMessage claimed, CancellationToken cancellationToken)
    {
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
    }

    private static DateTimeOffset ForwardDeadline(DateTimeOffset startedAt, InstantRailOptions opts) =>
        startedAt
        + TimeSpan.FromMilliseconds(opts.BudgetMilliseconds)
        - TimeSpan.FromMilliseconds(opts.CancelTimeoutMilliseconds);

    private InstantForwardResult Deferred(DateTimeOffset startedAt)
    {
        businessMetrics.RecordInstantPaymentDuration(
            BusinessMetrics.InstantPaymentOutcome.Deferred, timeProvider.GetUtcNow() - startedAt);
        return new InstantForwardResult(InstantDeliveryOutcome.Deferred, startedAt);
    }
}
