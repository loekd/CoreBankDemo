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
public class TransactionsController(ITransactionIntakeHandler handler, BusinessMetrics businessMetrics) : ControllerBase
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
            var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
            return BadRequest(new { Errors = errors });
        }

        var executeInline = string.Equals(
            Request.Headers[ExecuteModeHeader].FirstOrDefault(), ExecuteModeInline, StringComparison.OrdinalIgnoreCase);
        var priority = int.TryParse(
            Request.Headers[PaymentPriorityHeader].FirstOrDefault(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedPriority) && parsedPriority > MessageConstants.Priority.Standard
            ? parsedPriority
            : MessageConstants.Priority.Standard;

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
                BadRequest(new { Errors = result.Errors }),
            _ => throw new InvalidOperationException($"Unhandled transaction intake outcome: {result.Outcome}")
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
}
