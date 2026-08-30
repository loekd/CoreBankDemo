using CoreBankDemo.ServiceDefaults.Configuration;
using Dapr.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.ServiceDefaults;

/// <summary>
/// Dapr-backed <see cref="IEventPublisher"/>. Reaches <see cref="DaprClient"/>
/// only here (AD-6; the distributed-lock port's production adapter is
/// <see cref="RedisDistributedLockService"/> as of story 6.2/ADR-011, which
/// no longer touches Dapr at all), through its generic
/// <c>PublishEventAsync&lt;TData&gt;</c> pub/sub API. Pub/sub component and
/// topic name are DI-configured via <see cref="MessagingOutboxProcessingOptions"/>
/// (story 3.1), not passed per-call.
/// </summary>
/// <remarks>
/// Thin pass-through adapter: unlike <see cref="RedisDistributedLockService"/>,
/// this never catches or swallows exceptions from <see cref="DaprClient"/> —
/// they propagate to the caller unchanged. Failure classification belongs to
/// the Messaging kernel's <c>IOutboxDeliveryStrategy</c> contract (AD-11),
/// not this port.
/// </remarks>
public sealed class DaprEventPublisher(
    DaprClient daprClient,
    IOptions<MessagingOutboxProcessingOptions> options,
    ILogger<DaprEventPublisher> logger,
    BusinessMetrics businessMetrics) : IEventPublisher
{
    public async Task PublishAsync(
        string type,
        string source,
        string subject,
        object payload,
        string? traceParent,
        CancellationToken cancellationToken = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["cloudevent.type"] = type,
            ["cloudevent.source"] = source,
            ["cloudevent.subject"] = subject,
        };

        if (!string.IsNullOrWhiteSpace(traceParent))
        {
            metadata["cloudevent.traceparent"] = traceParent;
        }

        var pubSubName = options.Value.PubSubName;
        var topicName = options.Value.TopicName;

        logger.LogDebug(
            "Publishing CloudEvent {Type} to {PubSubName}/{TopicName} for subject {Subject}",
            type, pubSubName, topicName, subject);

        // Story 6.5 (AD-6/this class's the sole Dapr-send hook, chosen over
        // DaprOutboxDeliveryStrategy to avoid double counting): record only
        // after PublishEventAsync's outcome is known, and never swallow the
        // exception on failure — it still propagates unchanged to the caller
        // (the kernel's own retry/terminal-failure classification), matching
        // this class's existing no-catch contract.
        try
        {
            await daprClient.PublishEventAsync(pubSubName, topicName, payload, metadata, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            businessMetrics.RecordDelivery(
                BusinessMetrics.DeliveryDirection.Sent,
                BusinessMetrics.Transport.Dapr,
                ToMessageType(type),
                BusinessMetrics.DeliveryOutcome.Failed);
            throw;
        }

        businessMetrics.RecordDelivery(
            BusinessMetrics.DeliveryDirection.Sent,
            BusinessMetrics.Transport.Dapr,
            ToMessageType(type),
            BusinessMetrics.DeliveryOutcome.Succeeded);
    }

    /// <summary>
    /// Maps a CloudEvent <c>type</c> to the closed <see cref="BusinessMetrics.MessageType"/>
    /// vocabulary. <paramref name="type"/> is always one of the outgoing
    /// <see cref="CloudEventTypes.Constants"/> values chosen by this
    /// process's own <c>IOutboxDeliveryStrategy</c> — never copied verbatim
    /// from an incoming CloudEvent — but an unrecognized value still degrades
    /// to <see cref="BusinessMetrics.MessageType.Unknown"/> rather than
    /// throwing, so a future event type can never crash publication.
    /// </summary>
    private static BusinessMetrics.MessageType ToMessageType(string type) => type switch
    {
        CloudEventTypes.Constants.TransactionCompleted => BusinessMetrics.MessageType.TransactionCompleted,
        CloudEventTypes.Constants.TransactionFailed => BusinessMetrics.MessageType.TransactionFailed,
        CloudEventTypes.Constants.BalanceUpdated => BusinessMetrics.MessageType.BalanceUpdated,
        _ => BusinessMetrics.MessageType.Unknown
    };
}
