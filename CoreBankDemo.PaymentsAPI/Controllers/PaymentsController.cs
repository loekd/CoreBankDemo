using System.Text.Json;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace CoreBankDemo.PaymentsAPI.Controllers;

/// <summary>
/// Payment-intake HTTP surface (spec-5-2; instant rail additions per spec:
/// add-instant-payment-rail). Thin by design (conventions skill): bind,
/// check <see cref="ModelState"/>, call <see cref="IPaymentStorageHandler"/>
/// and (for <c>scheme=instant</c>) <see cref="IInstantPaymentForwardingHandler"/>,
/// and map results to an <see cref="IActionResult"/> -- no persistence,
/// idempotency, partitioning, rounding, tracing, clock, or budget/claim logic
/// here; all of that lives in the handlers.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class PaymentsController(
    IPaymentStorageHandler handler,
    IInstantPaymentForwardingHandler instantHandler,
    BusinessMetrics businessMetrics) : ControllerBase
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    [HttpPost]
    public async Task<IActionResult> ProcessPayment(
        [FromBody] PaymentRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            businessMetrics.RecordPaymentIntake(BusinessMetrics.PaymentOutcome.ValidationFailed, ToMetricScheme(request));
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
            PaymentStorageOutcome.Stored =>
                await ToStoredResultAsync(
                    result.Payment
                        ?? throw new InvalidOperationException(
                            $"Payment storage reported outcome '{result.Outcome}' without a persisted snapshot."),
                    request.Scheme,
                    cancellationToken),
            PaymentStorageOutcome.Duplicate =>
                ToDuplicateResult(
                    result.Payment
                        ?? throw new InvalidOperationException(
                            $"Payment storage reported outcome '{result.Outcome}' without a persisted snapshot."),
                    request.Scheme),
            PaymentStorageOutcome.ValidationFailed =>
                BadRequest(new { Errors = result.Errors }),
            _ => throw new InvalidOperationException($"Unhandled payment storage outcome: {result.Outcome}")
        };
    }

    /// <summary>
    /// A freshly-stored row on the standard rail keeps today's response
    /// exactly (<c>202</c>). On the instant rail, delegates the budgeted
    /// inline attempt to <see cref="IInstantPaymentForwardingHandler"/>: a
    /// committed outcome (business success or rejection) answers <c>200</c>
    /// with that outcome; anything deferred to the background rail answers
    /// <c>202</c> exactly like the standard rail.
    /// </summary>
    private async Task<IActionResult> ToStoredResultAsync(
        PaymentSnapshot snapshot, string scheme, CancellationToken cancellationToken)
    {
        if (!IsInstant(scheme))
        {
            return ToAcceptedResult(snapshot);
        }

        var forward = await instantHandler.ForwardAsync(snapshot, cancellationToken);
        return forward.Outcome switch
        {
            InstantDeliveryOutcome.Deferred => ToAcceptedResult(snapshot),
            InstantDeliveryOutcome.Completed or InstantDeliveryOutcome.Rejected =>
                Ok(ToInstantResponse(snapshot, forward)),
            _ => throw new InvalidOperationException($"Unhandled instant forward outcome: {forward.Outcome}")
        };
    }

    /// <summary>
    /// A duplicate key never triggers a second row or a second delivery
    /// attempt (spec: add-instant-payment-rail) -- it always replays the
    /// already-persisted snapshot. On the standard rail this reproduces
    /// today's behaviour exactly, byte-identically (always <c>202</c>, raw
    /// kernel <c>Status</c> verbatim via <see cref="ToAcceptedResult"/>/
    /// <see cref="ToResponse"/> -- untouched). On the instant rail, the wire
    /// contract instead replays <c>200</c> when the row is already
    /// <c>Completed</c> -- with <c>Status</c>/<c>ProcessedAt</c> derived from
    /// the persisted delivery outcome (<see cref="ResolveDeliveredResponse"/>),
    /// never the raw kernel column, which is transport-state-only and never
    /// distinguishes a business success from a business rejection (AD-11;
    /// review loop 1) -- a terminal <c>Failed</c> row (transport permanently
    /// exhausted via <c>MarkAsFailedWithRetryAsync</c>) replays <c>202</c>
    /// with the wire word <c>Failed</c>, never masked as still-in-flight --
    /// and anything else (<c>Pending</c>, or internally <c>Processing</c>
    /// under a live claim) replays <c>202</c> with the wire word <c>Pending</c>:
    /// that internal <c>Processing</c> value is never one of the outcomes
    /// this rail's wire contract promises for a not-yet-delivered duplicate
    /// (review loop 2).
    /// </summary>
    private IActionResult ToDuplicateResult(PaymentSnapshot snapshot, string scheme)
    {
        if (!IsInstant(scheme))
        {
            return ToAcceptedResult(snapshot);
        }

        if (snapshot.Status == MessageConstants.Status.Completed)
        {
            return Ok(ToDeliveredResponse(snapshot));
        }

        var wireStatus = snapshot.Status == MessageConstants.Status.Failed
            ? MessageConstants.Status.Failed
            : MessageConstants.Status.Pending;

        return Accepted(
            $"/api/payments/{Uri.EscapeDataString(snapshot.TransactionId)}",
            new PaymentResponse(
                snapshot.IdempotencyKey,
                snapshot.TransactionId,
                wireStatus,
                snapshot.Amount,
                snapshot.Currency,
                new DateTimeOffset(DateTime.SpecifyKind(snapshot.CreatedAt, DateTimeKind.Utc))));
    }

    private AcceptedResult ToAcceptedResult(PaymentSnapshot snapshot) =>
        Accepted(
            $"/api/payments/{Uri.EscapeDataString(snapshot.TransactionId)}",
            ToResponse(snapshot));

    private static bool IsInstant(string scheme) =>
        string.Equals(scheme, PaymentSchemes.Instant, StringComparison.Ordinal);

    private static BusinessMetrics.PaymentScheme ToMetricScheme(PaymentRequest request) =>
        IsInstant(request.Scheme) ? BusinessMetrics.PaymentScheme.Instant : BusinessMetrics.PaymentScheme.Standard;

    private static PaymentResponse ToResponse(PaymentSnapshot snapshot) => new(
        snapshot.IdempotencyKey,
        snapshot.TransactionId,
        snapshot.Status,
        snapshot.Amount,
        snapshot.Currency,
        new DateTimeOffset(DateTime.SpecifyKind(snapshot.CreatedAt, DateTimeKind.Utc)));

    /// <summary>
    /// Builds the wire response for a <c>Completed</c> instant-rail row,
    /// deriving both <c>Status</c> and <c>ProcessedAt</c> from the same
    /// single deserialization of the persisted delivery outcome
    /// (<see cref="ResolveDeliveredResponse"/>) rather than the row's raw
    /// kernel <c>Status</c> column/<c>CreatedAt</c> (review loop 1 and 2):
    /// deserializing once and reusing the result for both fields keeps them
    /// from ever disagreeing about which delivery attempt they describe.
    /// </summary>
    private static PaymentResponse ToDeliveredResponse(PaymentSnapshot snapshot)
    {
        var (status, processedAt) = ResolveDeliveredResponse(snapshot);
        return new PaymentResponse(
            snapshot.IdempotencyKey,
            snapshot.TransactionId,
            status,
            snapshot.Amount,
            snapshot.Currency,
            processedAt);
    }

    /// <summary>
    /// Recovers the actual committed business outcome
    /// (<see cref="TransactionSubmission.Status"/>: <c>Completed</c> vs
    /// <c>Failed</c>) and its real settlement
    /// <see cref="TransactionSubmission.ProcessedAt"/> from
    /// <see cref="PaymentSnapshot.ResponsePayload"/> -- the serialized
    /// <see cref="TransactionSubmission"/>
    /// <see cref="HttpForwardOutboxDeliveryStrategy.ForwardAsync"/> persists
    /// on every completed delivery (inline and background alike). Falls back
    /// to the row's raw <c>Completed</c> status and its <c>CreatedAt</c> if
    /// the payload is missing or corrupt -- should not happen going forward,
    /// but a duplicate replay must never crash over it (mirrors
    /// <c>TransactionIntakeHandler.TryDeserializeResponse</c>'s defensive
    /// fallback on the CoreBank side).
    /// </summary>
    private static (string Status, DateTimeOffset ProcessedAt) ResolveDeliveredResponse(PaymentSnapshot snapshot)
    {
        var fallbackProcessedAt = new DateTimeOffset(DateTime.SpecifyKind(snapshot.CreatedAt, DateTimeKind.Utc));

        if (string.IsNullOrEmpty(snapshot.ResponsePayload))
        {
            return (snapshot.Status, fallbackProcessedAt);
        }

        try
        {
            var submission = JsonSerializer.Deserialize<TransactionSubmission>(snapshot.ResponsePayload);
            return string.IsNullOrWhiteSpace(submission?.Status)
                ? (snapshot.Status, fallbackProcessedAt)
                : (submission.Status, submission.ProcessedAt);
        }
        catch (JsonException)
        {
            return (snapshot.Status, fallbackProcessedAt);
        }
    }

    private static PaymentResponse ToInstantResponse(PaymentSnapshot snapshot, InstantForwardResult forward) => new(
        snapshot.IdempotencyKey,
        snapshot.TransactionId,
        forward.Outcome == InstantDeliveryOutcome.Completed
            ? MessageConstants.Status.Completed
            : MessageConstants.Status.Failed,
        snapshot.Amount,
        snapshot.Currency,
        forward.ProcessedAt);
}
