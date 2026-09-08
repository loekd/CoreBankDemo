using System.Text.Json;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using Microsoft.Extensions.Logging;

namespace CoreBankDemo.PaymentsAPI.Outbox;

/// <summary>
/// Reusable validate-then-submit sequence against CoreBankAPI (spec:
/// add-instant-payment-rail's code map: "reuse the existing claim, deliver
/// and complete paths for the inline attempt ... do not copy it"). Extracted
/// from <see cref="HttpForwardOutboxDeliveryStrategy"/> so the instant-rail
/// forwarding handler shares exactly this logic -- including its AD-11
/// retry-outcome classification and its <c>corebankdemo.messaging.deliveries</c>
/// metric -- rather than reimplementing it: <see cref="HttpForwardOutboxDeliveryStrategy"/>
/// implements this interface itself and is registered under both it and
/// <see cref="IOutboxDeliveryStrategy{TMessage}"/> from the same scoped
/// instance. Unlike <see cref="IOutboxDeliveryStrategy{TMessage}.DeliverAsync"/>
/// (kernel-owned success/failure classification only -- AD-11), this returns
/// the actual <see cref="TransactionSubmission"/> so a caller that needs
/// CoreBank's committed business status (not just "did transport succeed")
/// can read it.
/// </summary>
internal interface ICoreBankTransactionForwarder
{
    /// <summary>
    /// Validates <paramref name="message"/>'s destination account, then
    /// submits it. <paramref name="executeInline"/> carries
    /// <c>X-Execute-Mode: inline</c> on the submission call only. Returns the
    /// submission on success (AD-11: any 2xx, including a business rejection
    /// -- CoreBank's <c>TransactionResponse.Status</c> distinguishes those,
    /// not this method's return path). Throws on any transport failure --
    /// never a business-rejection outcome, see AD-11 -- so a caller inherits
    /// the exact same retry-outcome classification
    /// <see cref="HttpForwardOutboxDeliveryStrategy.DeliverAsync"/> already
    /// has.
    /// </summary>
    Task<TransactionSubmission> ForwardAsync(OutboxMessage message, bool executeInline, CancellationToken cancellationToken);

    /// <summary>
    /// Asks CoreBankAPI to withdraw <paramref name="message"/> before it
    /// executes (spec: instant-rail-timeout-cancel). Returns CoreBank's
    /// answer -- <c>Cancelled</c> (provably dead), or the committed
    /// <c>Completed</c>/<c>Failed</c> outcome when it already executed -- and
    /// persists it as <see cref="OutboxMessage.ResponsePayload"/> exactly like
    /// <see cref="ForwardAsync"/> does, so the caller's next transition commits
    /// both. Returns <see langword="null"/> when nothing can be established:
    /// CoreBank refused (<c>409</c>: in flight or terminally failed), or the
    /// call failed on transport. Never throws for those; caller-requested
    /// cancellation still propagates.
    /// </summary>
    Task<TransactionSubmission?> CancelAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Forwards a stored payment to CoreBankAPI (story 5.4): validates the
/// destination account, then submits the transaction, using the sole
/// application-owned port <see cref="ICoreBankApiClient"/> (story 5.3).
/// Mirrors <c>CoreBankDemo.CoreBankAPI.Outbox.DaprOutboxDeliveryStrategy</c>'s
/// shape exactly — this strategy only decides delivery, never retry/backoff/
/// terminal-failure classification (that stays kernel-owned in
/// <c>OutboxProcessorBase&lt;TMessage&gt;</c>, AD-11).
///
/// <para>
/// Returns normally only when both calls report
/// <see cref="CoreBankClientOutcome.Success"/> — including a 200 duplicate-
/// accept replay from <see cref="ICoreBankApiClient.ProcessTransactionAsync"/>,
/// which <see cref="KiotaCoreBankApiClient"/> already classifies as
/// <see cref="CoreBankClientOutcome.Success"/> (story 5.3's Design Notes).
/// Throws for every other outcome — a <see cref="CoreBankClientOutcome.Retry"/>
/// from either call, or a <see cref="CoreBankClientOutcome.Success"/>
/// validation whose <see cref="AccountValidation.IsValid"/> is
/// <see langword="false"/> — so <c>OutboxProcessorBase&lt;TMessage&gt;</c>'s
/// existing <c>MarkAsFailedWithRetryAsync</c>/terminal-<c>Failed</c>-at-
/// <c>MaxRetryCount</c> path handles it exactly like any other delivery
/// failure. Never adds retry logic of its own.
/// </para>
///
/// <para>
/// The delivery sequence itself has no <c>try</c>/<c>catch</c>: an
/// <see cref="OperationCanceledException"/> raised by either
/// <see cref="ICoreBankApiClient"/> call (caller-requested cancellation) is
/// never caught here, so it propagates unchanged to
/// <c>OutboxProcessorBase&lt;TMessage&gt;</c>, which treats it as ordinary
/// cancellation — not a delivery failure — per its own contract. The one
/// guarded step is bookkeeping <em>after</em> a successful delivery
/// (persisting a replayed cancellation in <see cref="DeliverAsync"/>), which
/// must never be reported to the kernel as a delivery failure.
/// </para>
/// </summary>
internal sealed class HttpForwardOutboxDeliveryStrategy(
    ICoreBankApiClient client,
    IOutboxMessageStore<OutboxMessage> store,
    BusinessMetrics businessMetrics,
    ILogger<HttpForwardOutboxDeliveryStrategy> logger)
    : IOutboxDeliveryStrategy<OutboxMessage>, ICoreBankTransactionForwarder
{
    /// <summary>Recorded as <c>LastError</c> when the background rail learns from CoreBank that the command was cancelled.</summary>
    internal const string CancelledByCoreBankReason = "CoreBank reports the command was cancelled before execution";

    public async Task DeliverAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var submission = await ForwardAsync(message, executeInline: false, cancellationToken).ConfigureAwait(false);

        // spec: instant-rail-timeout-cancel's residual case. An instant
        // request whose own cancel timed out released this row to the
        // background rail while CoreBank had in fact stored the cancellation;
        // CoreBank now replays 200/Cancelled (a tombstone or cancelled inbox
        // row -- nothing executed). Delivery succeeded on transport, but the
        // truthful terminal state of this row is Cancelled, not Completed:
        // marking it here leaves the kernel's own MarkAsCompletedAsync an
        // AlreadyTerminal no-op, and keeps the duplicate replay and the
        // load-test gate consistent with CoreBank's row.
        if (submission.Status != MessageConstants.Status.Cancelled)
        {
            return;
        }

        // Persisted in its OWN try/catch, never sharing the delivery step's
        // failure classification (the same defect class review loop 2 guards
        // for completion persistence): delivery succeeded, so a failure here
        // must not be reported to the kernel as a delivery failure -- that
        // would burn a retry, redeliver, get 200/Cancelled again, and could
        // drive a provably cancelled command to terminal Failed. The row is
        // left exactly as claiming left it (Processing) to be reclaimed once
        // stale; CoreBank's replay is idempotent. Caller cancellation still
        // propagates.
        try
        {
            var transition = await store.MarkAsCancelledAsync(message, CancelledByCoreBankReason, cancellationToken)
                .ConfigureAwait(false);
            if (transition != MessageTransitionOutcome.Applied)
            {
                logger.LogWarning(
                    "CoreBank replayed a cancellation for payment {IdempotencyKey} but the outbox cancellation was not applied ({Transition})",
                    message.IdempotencyKey,
                    transition);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to persist the replayed cancellation of payment {IdempotencyKey} after a successful delivery; the row is left Processing for stale reclaim",
                message.IdempotencyKey);
        }
    }

    public async Task<TransactionSubmission?> CancelAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var result = await client
            .CancelTransactionAsync(
                new TransactionSubmissionRequest(
                    message.FromAccount,
                    message.ToAccount,
                    message.Amount,
                    message.Currency,
                    message.TransactionId,
                    message.Priority),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Outcome != CoreBankClientOutcome.Success)
        {
            // Conflict (in flight / terminally failed) or a transport
            // outcome: nothing can be established -- the caller keeps its
            // honest unknown. Never retried here; logged so the residual
            // 202 is explainable.
            if (result.Outcome == CoreBankClientOutcome.Conflict)
            {
                logger.LogInformation(
                    "CoreBank refused to cancel payment {IdempotencyKey}: the command is {Status} (409); outcome stays unknown",
                    message.IdempotencyKey,
                    result.Value?.Status);
            }
            else
            {
                logger.LogInformation(
                    "Cancel of payment {IdempotencyKey} established nothing: {RetryReason} (status {StatusCode}); outcome stays unknown",
                    message.IdempotencyKey,
                    result.RetryReason,
                    result.StatusCode);
            }

            return null;
        }

        message.ResponsePayload = JsonSerializer.Serialize(result.Value!);
        return result.Value!;
    }

    public async Task<TransactionSubmission> ForwardAsync(
        OutboxMessage message, bool executeInline, CancellationToken cancellationToken)
    {
        var validation = await client
            .ValidateAccountAsync(message.ToAccount, cancellationToken)
            .ConfigureAwait(false);

        if (validation.Outcome != CoreBankClientOutcome.Success)
        {
            throw RetryOutcomeException(
                "Destination account validation", validation.RetryReason, validation.StatusCode);
        }

        // A successful (2xx) validation call whose body says the account is
        // invalid is a successful business response, not a transport
        // failure (AD-11) -- but it is not a deliverable destination either,
        // so forwarding must not proceed to submission. Per the spec's
        // Boundaries, this is never anything other than a retry-then-
        // eventually-Failed outcome, decided here and left to the kernel.
        if (!validation.Value!.IsValid)
        {
            throw new InvalidOperationException(
                $"Destination account '{message.ToAccount}' failed validation (IsValid=false).");
        }

        var submission = await client
            .ProcessTransactionAsync(
                new TransactionSubmissionRequest(
                    message.FromAccount,
                    message.ToAccount,
                    message.Amount,
                    message.Currency,
                    message.TransactionId,
                    message.Priority),
                cancellationToken,
                executeInline)
            .ConfigureAwait(false);

        // Story 6.5: this is the sole concrete HTTP-send boundary for the
        // transaction command (account validation is a different message
        // shape, outside the closed message-type vocabulary, so it is never
        // tagged here). Recorded after the attempt's outcome is known, before
        // throwing on failure -- never for a caller-cancelled attempt, since
        // an OperationCanceledException raised by ProcessTransactionAsync
        // itself propagates straight out of the awaited call above without
        // ever reaching this line.
        businessMetrics.RecordDelivery(
            BusinessMetrics.DeliveryDirection.Sent,
            BusinessMetrics.Transport.Http,
            BusinessMetrics.MessageType.TransactionCommand,
            submission.Outcome == CoreBankClientOutcome.Success
                ? BusinessMetrics.DeliveryOutcome.Succeeded
                : BusinessMetrics.DeliveryOutcome.Failed);

        if (submission.Outcome != CoreBankClientOutcome.Success)
        {
            throw RetryOutcomeException(
                "Transaction submission", submission.RetryReason, submission.StatusCode);
        }

        // Review loop 1: persisted on EVERY completed delivery -- both this
        // background path (executeInline: false) and the instant-rail inline
        // path (executeInline: true) -- never an instant-only special case.
        // Mutates the same tracked entity the caller will next persist via
        // MarkAsCompletedAsync, so both fields commit together in one
        // SaveChanges call. AD-11's Status column stays transport-state-only;
        // this is what lets a later duplicate replay recover the actual
        // business outcome (settled vs. rejected) instead of always reading
        // back "Completed".
        message.ResponsePayload = JsonSerializer.Serialize(submission.Value!);

        return submission.Value!;
    }

    /// <summary>
    /// Builds the exception whose <see cref="Exception.Message"/> becomes the
    /// row's <c>LastError</c> — preserving the transport
    /// <see cref="CoreBankRetryReason"/> and, when present, the HTTP status
    /// CoreBankAPI actually returned (edge-case matrix: "status preserved in
    /// the message").
    /// </summary>
    private static InvalidOperationException RetryOutcomeException(
        string operation, CoreBankRetryReason? retryReason, int? statusCode) =>
        new($"{operation} failed: {retryReason}" +
            (statusCode is int code ? $" (status {code})" : string.Empty) +
            ".");
}
