using Dapr;
using Microsoft.AspNetCore.Mvc;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;

namespace CoreBankDemo.PaymentsAPI.Controllers;

[ApiController]
public class TransactionEventsController(ITransactionEventHandler handler) : ControllerBase
{
    private static readonly string[] AcceptedTypes =
    [
        Constants.TransactionCompleted,
        Constants.TransactionFailed
    ];

    [Topic("pubsub", "transaction-events")]
    [HttpPost("events/transactions")]
    public async Task<IActionResult> HandleTransactionCompleted(
        [FromBody] TransactionCompletedEvent transactionCompletedEvent,
        CancellationToken cancellationToken = default)
    {
        var response = await handler.HandleAsync(transactionCompletedEvent, cancellationToken);
        return Ok(response);
    }
}
