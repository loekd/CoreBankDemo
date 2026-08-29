using CoreBankDemo.Messaging;

namespace CoreBankDemo.PaymentsAPI.Outbox;

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
/// Deliberately has no <c>try</c>/<c>catch</c> anywhere: an
/// <see cref="OperationCanceledException"/> raised by either
/// <see cref="ICoreBankApiClient"/> call (caller-requested cancellation) is
/// never caught here, so it propagates unchanged to
/// <c>OutboxProcessorBase&lt;TMessage&gt;</c>, which treats it as ordinary
/// cancellation — not a delivery failure — per its own contract.
/// </para>
/// </summary>
internal sealed class HttpForwardOutboxDeliveryStrategy(ICoreBankApiClient client)
    : IOutboxDeliveryStrategy<OutboxMessage>
{
    public async Task DeliverAsync(OutboxMessage message, CancellationToken cancellationToken = default)
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
                    message.TransactionId),
                cancellationToken)
            .ConfigureAwait(false);

        if (submission.Outcome != CoreBankClientOutcome.Success)
        {
            throw RetryOutcomeException(
                "Transaction submission", submission.RetryReason, submission.StatusCode);
        }
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
