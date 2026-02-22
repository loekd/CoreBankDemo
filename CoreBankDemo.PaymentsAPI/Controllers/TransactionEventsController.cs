using Microsoft.AspNetCore.Mvc;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;

namespace CoreBankDemo.PaymentsAPI.Controllers;

[ApiController]
public class TransactionEventsController(ITransactionEventHandler handler, ILogger<TransactionEventsController> logger) : ControllerBase
{
    [HttpPost("events/transactions/completed")]
    public async Task<IActionResult> TransactionCompleted(
        [FromBody] TransactionCompletedEvent e,
        CancellationToken cancellationToken = default)
    {
        await handler.HandleAsync(e, cancellationToken);
        return Ok();
    }

    [HttpPost("events/transactions/failed")]
    public async Task<IActionResult> TransactionFailed(
        [FromBody] TransactionFailedEvent e,
        CancellationToken cancellationToken = default)
    {
        await handler.HandleAsync(e, cancellationToken);
        return Ok();
    }

    [HttpPost("events/transactions/balance-updated")]
    public async Task<IActionResult> BalanceUpdated(
        [FromBody] BalanceUpdatedEvent e,
        CancellationToken cancellationToken = default)
    {
        await handler.HandleAsync(e, cancellationToken);
        return Ok();
    }

    [HttpPost("events/transactions/unknown")]
    public IActionResult Unknown()
    {
        logger.LogWarning("Received unknown cloud event type, ignoring");
        return Ok();
    }
}
