namespace CoreBankDemo.PaymentsAPI.Outbox;

/// <summary>
/// Transport-only outcome for every <see cref="ICoreBankApiClient"/> call
/// (story 5.3; AD-11). <see cref="Success"/> means CoreBankAPI answered with
/// a 2xx and a well-formed body — including a business rejection such as
/// <c>IsValid = false</c> or a <c>Failed</c> transaction status, both of
/// which are successful business responses over a 2xx transport (see the
/// spec's Design Notes). <see cref="Retry"/> covers every non-2xx response,
/// empty/malformed required success data, a timeout, or any other exception
/// not caused by the caller's own cancellation. It is deliberately silent
/// about what a retry outcome should mean for a message's delivery
/// <c>Status</c> — story 5.4's <c>IOutboxDeliveryStrategy</c> decides that,
/// this port only classifies — but it does preserve the transport
/// status/diagnostic context the edge-case matrix requires
/// (<see cref="CoreBankResult{T}.RetryReason"/>,
/// <see cref="CoreBankResult{T}.StatusCode"/>), never a generated type,
/// response body, or other potentially sensitive payload. Caller-requested
/// cancellation is never represented here — it always propagates as an
/// <see cref="OperationCanceledException"/> instead.
/// </summary>
internal enum CoreBankClientOutcome
{
    Success,
    Retry
}

/// <summary>
/// Coarse, application-owned classification of why a
/// <see cref="CoreBankClientOutcome.Retry"/> outcome occurred (edge-case
/// matrix: "Preserve status/diagnostic context without generated types").
/// Deliberately does not carry response bodies, headers, or any
/// Kiota-generated type — only enough shape for a caller to distinguish the
/// four transport-failure classes.
/// </summary>
internal enum CoreBankRetryReason
{
    /// <summary>CoreBankAPI answered with a non-2xx status; see <see cref="CoreBankResult{T}.StatusCode"/>.</summary>
    TransportRejection,

    /// <summary>A 2xx response arrived but required success data was missing or malformed.</summary>
    MalformedResponse,

    /// <summary>The call timed out (e.g. <c>HttpClient.Timeout</c>) independently of the caller's own cancellation.</summary>
    Timeout,

    /// <summary>Any other transport exception (connection reset, DNS failure, etc.).</summary>
    TransportException
}

/// <summary>
/// Uniform result envelope every <see cref="ICoreBankApiClient"/> method
/// returns. <see cref="Value"/> is populated only when <see cref="Outcome"/>
/// is <see cref="CoreBankClientOutcome.Success"/>. <see cref="RetryReason"/>
/// and <see cref="StatusCode"/> are populated only when <see cref="Outcome"/>
/// is <see cref="CoreBankClientOutcome.Retry"/>; <see cref="StatusCode"/> is
/// further only ever set for <see cref="CoreBankRetryReason.TransportRejection"/>
/// (the frozen wire status CoreBankAPI actually returned) — never a
/// response body or generated error type. The primary constructor is
/// private: <see cref="Success"/> and <see cref="Retry"/> are the only ways
/// to build one, so a contradictory state (e.g. <see cref="Success"/> with a
/// <see cref="RetryReason"/>, or <see cref="Outcome"/> disagreeing with
/// <see cref="Value"/>) cannot be constructed. Still a normal
/// value-equality/<see cref="object.ToString"/> record — only the
/// positional-style public constructor is removed.
/// </summary>
internal sealed record CoreBankResult<T>
    where T : class
{
    public CoreBankClientOutcome Outcome { get; }
    public T? Value { get; }
    public CoreBankRetryReason? RetryReason { get; }
    public int? StatusCode { get; }

    private CoreBankResult(
        CoreBankClientOutcome outcome, T? value, CoreBankRetryReason? retryReason, int? statusCode)
    {
        Outcome = outcome;
        Value = value;
        RetryReason = retryReason;
        StatusCode = statusCode;
    }

    public static CoreBankResult<T> Success(T value) =>
        new(CoreBankClientOutcome.Success, value, retryReason: null, statusCode: null);

    public static CoreBankResult<T> Retry(CoreBankRetryReason reason, int? statusCode = null) =>
        new(CoreBankClientOutcome.Retry, value: default, reason, statusCode);
}

/// <summary>
/// Application-owned mirror of CoreBankAPI's <c>AccountValidationResponse</c>
/// (frozen wire shape: <c>CoreBankDemo.CoreBankAPI/Models/AccountValidationResponse.cs</c>).
/// </summary>
internal sealed record AccountValidation(
    string AccountNumber,
    bool IsValid,
    string? AccountHolderName,
    decimal? Balance);

/// <summary>
/// Application-owned mirror of CoreBankAPI's <c>AccountDetailsResponse</c>
/// (frozen wire shape: <c>CoreBankDemo.CoreBankAPI/Models/AccountDetailsResponse.cs</c>).
/// </summary>
internal sealed record AccountDetails(
    string AccountNumber,
    string AccountHolderName,
    decimal Balance,
    string Currency,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// Application-owned input for <see cref="ICoreBankApiClient.ProcessTransactionAsync"/>,
/// mirroring CoreBankAPI's frozen <c>TransactionRequest</c> fields
/// (<c>CoreBankDemo.CoreBankAPI/Models/TransactionRequest.cs</c>). Deliberately
/// independent of PaymentsAPI's own <see cref="OutboxMessage"/> shape — this
/// port never depends on how a caller sources these values.
/// </summary>
internal sealed record TransactionSubmissionRequest(
    string FromAccount,
    string ToAccount,
    decimal Amount,
    string Currency,
    string TransactionId);

/// <summary>
/// Application-owned mirror of CoreBankAPI's <c>TransactionResponse</c>
/// (frozen wire shape: <c>CoreBankDemo.CoreBankAPI/Models/TransactionResponse.cs</c>),
/// returned for both a freshly-accepted/in-flight submission (202) and a
/// completed duplicate replaying its cached response (200) — both are
/// <see cref="CoreBankClientOutcome.Success"/>; see the spec's Design Notes.
/// </summary>
internal sealed record TransactionSubmission(
    string TransactionId,
    string Status,
    DateTimeOffset ProcessedAt);

/// <summary>
/// Application-owned mirror of the <c>GET /api/transactions/{idempotencyKey}</c>
/// 200 payload, which represents either a cached <c>TransactionResponse</c> or
/// a <c>TransactionStatusResponse</c> snapshot depending on CoreBankAPI's
/// current state for that key (see <c>corebank-api.json</c>'s
/// <c>TransactionQueryResult</c> schema and CoreBankAPI's
/// <c>Models/TransactionStatusResponse.cs</c>). <see cref="ReceivedAt"/> and
/// <see cref="ProcessedAt"/> are optional because only one variant carries
/// each field on the wire.
/// </summary>
internal sealed record TransactionStatus(
    string TransactionId,
    string Status,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? ProcessedAt);
