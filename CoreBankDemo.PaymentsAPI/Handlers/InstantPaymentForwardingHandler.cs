using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults;
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
    IOptions<InstantRailOptions> options,
    TimeProvider timeProvider,
    ILogger<InstantPaymentForwardingHandler> logger,
    BusinessMetrics businessMetrics) : IInstantPaymentForwardingHandler
{
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

        var claimed = await store.TryClaimByIdAsync(payment.Id, cancellationToken).ConfigureAwait(false);
        if (claimed is null)
        {
            logger.LogInformation(
                "Instant rail: payment {IdempotencyKey} row was not claimable (already claimed or not pending); deferred to background delivery",
                payment.IdempotencyKey);
            return Deferred(startedAt);
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

                var settled = string.Equals(submission.Status, MessageConstants.Status.Completed, StringComparison.Ordinal);
                var outcome = settled ? InstantDeliveryOutcome.Completed : InstantDeliveryOutcome.Rejected;

                businessMetrics.RecordInstantPaymentDuration(
                    settled ? BusinessMetrics.InstantPaymentOutcome.Settled : BusinessMetrics.InstantPaymentOutcome.Rejected,
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
