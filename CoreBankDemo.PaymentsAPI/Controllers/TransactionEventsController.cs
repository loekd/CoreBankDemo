using Dapr;
using Microsoft.AspNetCore.Mvc;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;

namespace CoreBankDemo.PaymentsAPI.Controllers;

[ApiController]
public class TransactionEventsController(ITransactionEventHandler handler) : ControllerBase
{
    [Topic("pubsub", "transaction-events")]
    [HttpPost("events/transactions")]
    public async Task<IActionResult> HandleTransactionCompleted(
        [FromBody] TransactionCompletedEvent transactionCompletedEvent,
        CancellationToken cancellationToken = default)
    {
        // OpenTelemetry AspNetCore instrumentation automatically extracts traceparent/tracestate from HTTP headers
        // The handler will create a span that's automatically linked to the parent trace
        var response = await handler.HandleAsync(transactionCompletedEvent, cancellationToken);
        return Ok(response);
    }
}
