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
    /// </summary>
    Task<CoreBankResult<TransactionSubmission>> ProcessTransactionAsync(
        TransactionSubmissionRequest request, CancellationToken cancellationToken, bool executeInline = false);

    Task<CoreBankResult<TransactionStatus>> GetTransactionStatusAsync(
        string idempotencyKey, CancellationToken cancellationToken);
}
