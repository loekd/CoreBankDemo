namespace CoreBankDemo.PaymentsAPI.Outbox;

/// <summary>
/// A retry outcome from <see cref="ICoreBankApiClient"/> surfaced by
/// <see cref="HttpForwardOutboxDeliveryStrategy"/> (ADR-024). Still an
/// <see cref="InvalidOperationException"/> with the exact message the
/// background kernel has always stored as <c>LastError</c>, so nothing
/// downstream changes; the typed members exist for the instant rail, which
/// needs the server's <see cref="RetryAfter"/> to decide how long to wait.
/// </summary>
internal sealed class CoreBankRetryException(
    string operation,
    CoreBankRetryReason? retryReason,
    int? statusCode,
    TimeSpan? retryAfter)
    : InvalidOperationException(BuildMessage(operation, retryReason, statusCode))
{
    public CoreBankRetryReason? RetryReason { get; } = retryReason;

    /// <summary>The HTTP status CoreBankAPI actually returned, when there was one.</summary>
    public int? StatusCode { get; } = statusCode;

    /// <summary>The server's <c>Retry-After</c> on a <c>429</c>/<c>503</c>; <see langword="null"/> otherwise.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    // Byte-identical to the message the strategy built before ADR-024
    // (edge-case matrix: "status preserved in the message").
    private static string BuildMessage(string operation, CoreBankRetryReason? retryReason, int? statusCode) =>
        $"{operation} failed: {retryReason}" +
        (statusCode is int code ? $" (status {code})" : string.Empty) +
        ".";
}
