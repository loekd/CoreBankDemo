namespace CoreBankDemo.PaymentsAPI.Outbox;

/// <summary>
/// Sole application-owned port onto CoreBankAPI (story 5.3). Every one of
/// CoreBankAPI's four public operations is available here through
/// application-owned request/result types only — no Kiota-generated model
/// ever appears in this signature or crosses it. Implemented by
/// <see cref="KiotaCoreBankApiClient"/>; nothing else in this codebase is
/// permitted to talk to CoreBankAPI (no hand-written HTTP client, no Dapr
/// client, and no dead transport-selector flag — see the spec's
/// boundaries and ADR-008).
/// </summary>
internal interface ICoreBankApiClient
{
    Task<CoreBankResult<AccountValidation>> ValidateAccountAsync(
        string accountNumber, CancellationToken cancellationToken);

    Task<CoreBankResult<AccountDetails>> GetAccountDetailsAsync(
        string accountNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Submits <paramref name="request"/>. <paramref name="executeInline"/>
    /// carries <c>X-Execute-Mode: inline</c> on the wire only when
    /// <see langword="true"/> (spec: add-instant-payment-rail) -- absent
    /// (the default) reproduces today's deferred-execution request exactly.
    /// A non-standard <see cref="TransactionSubmissionRequest.Priority"/> is
    /// carried as <c>X-Payment-Priority</c> so CoreBankAPI queues the command
    /// at the same priority; the standard rail never sends that header.
    /// </summary>
    Task<CoreBankResult<TransactionSubmission>> ProcessTransactionAsync(
        TransactionSubmissionRequest request, CancellationToken cancellationToken, bool executeInline = false);

    Task<CoreBankResult<TransactionStatus>> GetTransactionStatusAsync(
        string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Withdraws <paramref name="request"/> before CoreBankAPI executes it
    /// (<c>POST /api/transactions/cancel</c>, spec: instant-rail-timeout-cancel).
    /// <see cref="CoreBankClientOutcome.Success"/> carries CoreBank's answer:
    /// <c>Cancelled</c> when the command is provably dead, or the committed
    /// <c>Completed</c>/<c>Failed</c> outcome when it already executed.
    /// <see cref="CoreBankClientOutcome.Conflict"/> is CoreBank's <c>409</c>
    /// (in flight or terminally failed -- not cancellable), with the reported
    /// status in <see cref="CoreBankResult{T}.Value"/>. Everything else is a
    /// <see cref="CoreBankClientOutcome.Retry"/> classification exactly as for
    /// <see cref="ProcessTransactionAsync"/>. Carries <c>X-Payment-Priority</c>
    /// under the same rule as the submission call.
    /// </summary>
    Task<CoreBankResult<TransactionSubmission>> CancelTransactionAsync(
        TransactionSubmissionRequest request, CancellationToken cancellationToken);
}
