using System.Text.Json;
using Dapr;
using Microsoft.AspNetCore.Mvc;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;

namespace CoreBankDemo.PaymentsAPI.Controllers;

[ApiController]
public class TransactionEventsController(ITransactionEventHandler handler, ILogger<TransactionEventsController> logger) : ControllerBase
{
    // A single endpoint subscribes to the topic and routes internally by CloudEvent type.
    // UseCloudEvents() middleware unwraps the envelope and puts the 'data' in the body,
    // but we need the 'type' field too — so we read from the raw CloudEvent envelope
    // by accessing the custom header that Dapr injects: 'ce-type'.
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    [Topic("pubsub", "transaction-events")]
    [HttpPost("events/transactions")]
    public async Task<IActionResult> HandleTransactionEvent(
        [FromBody] JsonElement data,
        CancellationToken cancellationToken = default)
    {
        // Dapr injects CloudEvent attributes as 'Cloudevent.{attribute}' HTTP headers.
        var eventType = HttpContext.Request.Headers["Cloudevent.type"].FirstOrDefault();

        logger.LogInformation("Received cloud event of type {EventType}", eventType);

        // UseCloudEvents() middleware may leave the data field as a JSON-encoded string
        // rather than an object. Unwrap it to a raw JSON string before deserializing.
        var json = data.ValueKind == JsonValueKind.String
            ? data.GetString()!
            : data.GetRawText();

        switch (eventType)
        {
            case Constants.TransactionCompleted:
            case Constants.TransactionFailed:
            {
                var transactionEvent = JsonSerializer.Deserialize<TransactionCompletedEvent>(json, CaseInsensitive);
                if (transactionEvent is null)
                    return BadRequest("Failed to deserialize TransactionCompletedEvent");
                await handler.HandleAsync(transactionEvent, cancellationToken);
                break;
            }
            case Constants.BalanceUpdated:
            {
                var balanceEvent = JsonSerializer.Deserialize<BalanceUpdatedEvent>(json, CaseInsensitive);
                if (balanceEvent is null)
                    return BadRequest("Failed to deserialize BalanceUpdatedEvent");
                await handler.HandleAsync(balanceEvent, cancellationToken);
                break;
            }
            default:
                logger.LogWarning("Received unknown cloud event type {EventType}, ignoring", eventType);
                break;
        }

        return Ok(new { status = "SUCCESS" });
    }
}
