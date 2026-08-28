using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Models;
using Microsoft.AspNetCore.Mvc;

namespace CoreBankDemo.PaymentsAPI.Controllers;

/// <summary>
/// Payment-intake HTTP surface (spec-5-2). Thin by design (conventions
/// skill): bind, check <see cref="ModelState"/>, call
/// <see cref="IPaymentStorageHandler"/>, and map its result to an
/// <see cref="IActionResult"/> -- no persistence, idempotency, partitioning,
/// rounding, tracing, or clock logic here; all of that lives in the handler.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class PaymentsController(IPaymentStorageHandler handler) : ControllerBase
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    [HttpPost]
    public async Task<IActionResult> ProcessPayment(
        [FromBody] PaymentRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values
                .SelectMany(value => value.Errors)
                .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage)
                    ? "The request is invalid."
                    : error.ErrorMessage);
            return BadRequest(new { Errors = errors });
        }

        var idempotencyKey = Request.Headers[IdempotencyKeyHeader].FirstOrDefault();
        var result = await handler.StoreAsync(request, idempotencyKey, cancellationToken);

        return result.Outcome switch
        {
            PaymentStorageOutcome.Stored or PaymentStorageOutcome.Duplicate =>
                ToAcceptedResult(
                    result.Payment
                        ?? throw new InvalidOperationException(
                            $"Payment storage reported outcome '{result.Outcome}' without a persisted snapshot.")),
            PaymentStorageOutcome.ValidationFailed =>
                BadRequest(new { Errors = result.Errors }),
            _ => throw new InvalidOperationException($"Unhandled payment storage outcome: {result.Outcome}")
        };
    }

    private AcceptedResult ToAcceptedResult(PaymentSnapshot snapshot) =>
        Accepted(
            $"/api/payments/{Uri.EscapeDataString(snapshot.TransactionId)}",
            ToResponse(snapshot));

    private static PaymentResponse ToResponse(PaymentSnapshot snapshot) => new(
        snapshot.IdempotencyKey,
        snapshot.TransactionId,
        snapshot.Status,
        snapshot.Amount,
        snapshot.Currency,
        new DateTimeOffset(DateTime.SpecifyKind(snapshot.CreatedAt, DateTimeKind.Utc)));
}
