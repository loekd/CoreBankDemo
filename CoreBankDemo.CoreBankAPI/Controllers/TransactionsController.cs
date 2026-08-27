using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
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
public class TransactionsController(ITransactionIntakeHandler handler) : ControllerBase
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

        var result = await handler.ProcessAsync(request, cancellationToken);

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
