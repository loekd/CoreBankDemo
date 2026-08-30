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
    [HttpPost("process")]
    public async Task<IActionResult> ProcessTransaction(
        [FromBody] TransactionRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
            return BadRequest(new { Errors = errors });
        }

        TransactionIntakeResult result;
        try
        {
            result = await handler.ProcessAsync(request, cancellationToken);
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
                TransactionIntakeOutcome.Replayed => BusinessMetrics.DeliveryOutcome.Duplicate,
                TransactionIntakeOutcome.InFlight => BusinessMetrics.DeliveryOutcome.Duplicate,
                TransactionIntakeOutcome.TransportFailed => BusinessMetrics.DeliveryOutcome.Failed,
                _ => throw new InvalidOperationException($"Unhandled transaction intake outcome: {result.Outcome}")
            });

        return result.Outcome switch
        {
            TransactionIntakeOutcome.Accepted =>
                Accepted($"/api/transactions/{request.TransactionId}", result.Response),
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
