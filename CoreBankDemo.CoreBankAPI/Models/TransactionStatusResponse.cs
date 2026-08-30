namespace CoreBankDemo.CoreBankAPI.Models;

/// <summary>
/// Status-reporting shape for <c>GET /api/transactions/{idempotencyKey}</c>
/// when the underlying inbox row is not a <c>Completed</c> row with a cached
/// payload. As of spec-5-3, this shape mirrors <c>TransactionQueryResult</c>
/// in the checked-in <c>corebank-api.json</c> contract, so it is now frozen
/// like <see cref="TransactionResponse"/>: any change requires an ADR that
/// renegotiates the wire contract (AD-12) and a regenerated Kiota client.
/// </summary>
public record TransactionStatusResponse(
    string TransactionId,
    string Status,
    DateTime ReceivedAt,
    DateTime? ProcessedAt
);
