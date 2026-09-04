namespace CoreBankDemo.PaymentsAPI.Models;

/// <summary>
/// Frozen payment acknowledgement contract (spec-5-2, epics.md:52). Restored
/// verbatim -- do not add, remove, rename, or reorder fields without an ADR.
/// </summary>
public record PaymentResponse(
    string PaymentId,
    string TransactionId,
    string Status,
    decimal Amount,
    string Currency,
    DateTimeOffset ProcessedAt
);
