namespace CoreBankDemo.PaymentsAPI;

/// <summary>
/// Span tags that mark the point where a payment provably ends without
/// settling, so the traces dashboard's "Failed payments" table can list
/// <em>which payment failed, and why</em> without a service or operation
/// filter. Set only when PaymentsAPI's own row commits the outcome (a
/// cancelled outbox row, or a <c>transaction.failed</c> event recorded
/// against it) -- never on a cancel that lost its race to execution, which
/// ends <c>Completed</c> and is not a failed payment.
/// </summary>
internal static class FailedPaymentTags
{
    /// <summary><see cref="Cancelled"/> or <see cref="Rejected"/>.</summary>
    public const string Outcome = "payment.outcome";

    /// <summary>The reason recorded on the row (<c>LastError</c>) or carried by the event.</summary>
    public const string FailureReason = "payment.failure_reason";

    /// <summary>The command's transaction id, so the table row names the payment.</summary>
    public const string TransactionId = "transaction.id";

    /// <summary>Withdrawn before execution; no money moved.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>CoreBank executed the command and a business rule rejected it.</summary>
    public const string Rejected = "rejected";
}
