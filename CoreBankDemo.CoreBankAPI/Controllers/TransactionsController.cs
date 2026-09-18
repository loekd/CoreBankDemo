using CoreBankDemo.Messaging;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace CoreBankDemo.CoreBankAPI.Controllers;

/// <summary>
/// Transaction-intake HTTP surface (spec-4-4). Thin by design (conventions
/// skill, AD-2): bind, check <see cref="ModelState"/>, call
/// <see cref="ITransactionIntakeHandler"/>, map its result to an
/// <see cref="IActionResult"/> — no dedupe branching, payload deserialization,
/// or activity enrichment here; all of that lives in the handler.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class TransactionsController(
    ITransactionIntakeHandler handler,
    ITransactionCancellationHandler cancellationHandler,
    ITransactionRejectionHandler rejectionHandler,
    BusinessMetrics businessMetrics) : ControllerBase
{
    /// <summary>
    /// Optional inline-execution opt-in (spec: add-instant-payment-rail).
    /// Absent reproduces today's deferred-execution behaviour exactly.
    /// </summary>
    private const string ExecuteModeHeader = "X-Execute-Mode";
    private const string ExecuteModeInline = "inline";

    /// <summary>
    /// Optional claim priority for the stored command (see
    /// <see cref="MessageConstants.Priority"/>). PaymentsAPI sends it for the
    /// instant rail only; absent, unparsable or non-positive means standard,
    /// so a caller can never make a command wait *longer* by sending garbage.
    /// </summary>
    private const string PaymentPriorityHeader = "X-Payment-Priority";

    [HttpPost("process")]
    public async Task<IActionResult> ProcessTransaction(
        [FromBody] TransactionRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToArray();

            // ADR-023: a 400 is a verdict, so it is recorded and published
            // (transaction.failed, one save) before it is given. If that
            // cannot be done the honest answer is 503 -- the caller retries.
            var rejection = await rejectionHandler.RejectAsync(request, errors, cancellationToken);
            return rejection == TransactionRejectionOutcome.StoreFailed
                ? ServiceUnavailable(["The rejection could not be recorded; retry"])
                : BadRequest(new { Errors = errors });
        }

        var executeInline = string.Equals(
            Request.Headers[ExecuteModeHeader].FirstOrDefault(), ExecuteModeInline, StringComparison.OrdinalIgnoreCase);
        var priority = ReadPriorityHeader();

        TransactionIntakeResult result;
        try
        {
            result = await handler.ProcessAsync(request, cancellationToken, executeInline, priority);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            businessMetrics.RecordDelivery(
                BusinessMetrics.DeliveryDirection.Received,
                BusinessMetrics.Transport.Http,
                BusinessMetrics.MessageType.TransactionCommand,
                BusinessMetrics.DeliveryOutcome.Failed);
            throw;
        }

        // Story 6.5: the concrete HTTP-receive boundary for the transaction
        // command. Recorded from the already-known intake outcome rather
        // than re-deriving it, so this can never disagree with the
        // transaction-intake measurement the handler already recorded.
        businessMetrics.RecordDelivery(
            BusinessMetrics.DeliveryDirection.Received,
            BusinessMetrics.Transport.Http,
            BusinessMetrics.MessageType.TransactionCommand,
            result.Outcome switch
            {
                TransactionIntakeOutcome.Accepted => BusinessMetrics.DeliveryOutcome.Succeeded,
                TransactionIntakeOutcome.InlineCompleted => BusinessMetrics.DeliveryOutcome.Succeeded,
                TransactionIntakeOutcome.Replayed => BusinessMetrics.DeliveryOutcome.Duplicate,
                TransactionIntakeOutcome.InFlight => BusinessMetrics.DeliveryOutcome.Duplicate,
                TransactionIntakeOutcome.TransportFailed => BusinessMetrics.DeliveryOutcome.Failed,
                _ => throw new InvalidOperationException($"Unhandled transaction intake outcome: {result.Outcome}")
            });

        return result.Outcome switch
        {
            TransactionIntakeOutcome.Accepted =>
                Accepted($"/api/transactions/{request.TransactionId}", result.Response),
            // Inline execution committed within this request (spec:
            // add-instant-payment-rail): the final TransactionResponse is
            // already known, so this answers 200 instead of 202 -- unlike
            // Accepted above.
            TransactionIntakeOutcome.InlineCompleted =>
                Ok(result.Response),
            TransactionIntakeOutcome.Replayed =>
                Ok(result.Response),
            // AD-11: an in-flight duplicate reports current status with 202,
            // same as a freshly-accepted request.
            TransactionIntakeOutcome.InFlight =>
                Accepted($"/api/transactions/{request.TransactionId}", result.Response),
            TransactionIntakeOutcome.TransportFailed =>
                ServiceUnavailable(result.Errors),
            _ => throw new InvalidOperationException($"Unhandled transaction intake outcome: {result.Outcome}")
        };
    }

    /// <summary>
    /// Instant-rail cancellation (spec: instant-rail-timeout-cancel). The body
    /// is the original <see cref="TransactionRequest"/>. <c>200</c> carries a
    /// <c>Cancelled</c> response when the command is provably dead, or the
    /// committed <see cref="TransactionResponse"/> when CoreBank already
    /// executed it; <c>409</c> carries the current status when the row cannot
    /// be cancelled (in flight); <c>503</c> when the tombstone could not be
    /// stored. Never touches the ledger.
    /// </summary>
    [HttpPost("cancel")]
    public async Task<IActionResult> CancelTransaction(
        [FromBody] TransactionRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
            return BadRequest(new { Errors = errors });
        }

        var result = await cancellationHandler.CancelAsync(request, cancellationToken, ReadPriorityHeader());

        return result.Outcome switch
        {
            TransactionCancellationOutcome.Cancelled => Ok(result.Response),
            TransactionCancellationOutcome.AlreadyCommitted => Ok(result.Response),
            TransactionCancellationOutcome.InFlight => Conflict(result.Response),
            TransactionCancellationOutcome.StoreFailed => ServiceUnavailable(result.Errors),
            _ => throw new InvalidOperationException($"Unhandled transaction cancellation outcome: {result.Outcome}")
        };
    }

    [HttpGet("{idempotencyKey}")]
    public async Task<IActionResult> GetTransactionStatus(string idempotencyKey, CancellationToken cancellationToken)
    {
        var result = await handler.GetStatusAsync(idempotencyKey, cancellationToken);

        if (!result.Found)
        {
            return NotFound(new { Errors = new[] { "Transaction not found" } });
        }

        return result.CachedResponse is not null
            ? Ok(result.CachedResponse)
            : Ok(result.StatusResponse);
    }

    /// <summary>An internal failure is not a bad request (ADR-023): the caller is asked to retry.</summary>
    private ObjectResult ServiceUnavailable(IEnumerable<string>? errors) =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new { Errors = errors ?? [] });

    private int ReadPriorityHeader() =>
        int.TryParse(
            Request.Headers[PaymentPriorityHeader].FirstOrDefault(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedPriority) && parsedPriority > MessageConstants.Priority.Standard
            ? parsedPriority
            : MessageConstants.Priority.Standard;
}
