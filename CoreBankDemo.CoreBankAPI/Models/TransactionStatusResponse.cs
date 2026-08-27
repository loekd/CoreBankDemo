namespace CoreBankDemo.CoreBankAPI.Models;

/// <summary>
/// Status-reporting shape for <c>GET /api/transactions/{idempotencyKey}</c>
/// when the underlying inbox row is not a <c>Completed</c> row with a cached
/// payload (spec-4-4: "free to shape" — not frozen by any AD, unlike
/// <see cref="TransactionResponse"/>).
/// </summary>
public record TransactionStatusResponse(
    string TransactionId,
    string Status,
    DateTime ReceivedAt,
    DateTime? ProcessedAt
);
