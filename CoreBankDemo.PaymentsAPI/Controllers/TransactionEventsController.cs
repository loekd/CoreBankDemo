using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using Microsoft.AspNetCore.Mvc;

namespace CoreBankDemo.PaymentsAPI.Controllers;

/// <summary>
/// The frozen <c>transaction-events</c> subscription surface (spec-5-5):
/// routes must stay aligned with both
/// <c>dapr/components/subscription-transaction-events.yaml</c> and
/// <c>dapr/components-loadtest/subscription-transaction-events.yaml</c>.
/// Thin by design (conventions skill): bind the CloudEvent's unwrapped data
/// (<c>Dapr.AspNetCore</c>'s cloud-events middleware, wired in
/// <c>Program.cs</c>, already stripped the envelope) and delegate to <see
/// cref="ITransactionEventIntakeHandler"/> -- no persistence, partitioning,
/// serialization, or clock logic here. Each known-event action returns 200
/// only after the handler's storage call completes; the default/unknown
/// route never stores anything, only logs a warning, and still
/// acknowledges with 200 so Dapr never redelivers a type this service will
/// never recognize.
/// </summary>
[ApiController]
public class TransactionEventsController(
    ITransactionEventIntakeHandler handler,
    ILogger<TransactionEventsController> logger) : ControllerBase
{
    [HttpPost("events/transactions/completed")]
    public async Task<IActionResult> TransactionCompleted(
        [FromBody] TransactionCompletedEvent transactionCompleted,
        CancellationToken cancellationToken)
    {
        await handler.StoreAsync(transactionCompleted, cancellationToken);
        return Ok();
    }

    [HttpPost("events/transactions/failed")]
    public async Task<IActionResult> TransactionFailed(
        [FromBody] TransactionFailedEvent transactionFailed,
        CancellationToken cancellationToken)
    {
        await handler.StoreAsync(transactionFailed, cancellationToken);
        return Ok();
    }

    [HttpPost("events/transactions/balance-updated")]
    public async Task<IActionResult> BalanceUpdated(
        [FromBody] BalanceUpdatedEvent balanceUpdated,
        CancellationToken cancellationToken)
    {
        await handler.StoreAsync(balanceUpdated, cancellationToken);
        return Ok();
    }

    [HttpPost("events/transactions/unknown")]
    public IActionResult Unknown(
        [FromHeader(Name = "Cloudevent.type")] string? eventType,
        [FromHeader(Name = "Cloudevent.id")] string? eventId,
        [FromHeader(Name = "Cloudevent.source")] string? source)
    {
        logger.LogWarning(
            "Received unsupported transaction-events CloudEvent {EventId} of type {EventType} from {EventSource}; acknowledging without storage",
            eventId,
            eventType,
            source);
        return Ok();
    }
}
