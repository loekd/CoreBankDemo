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
    ILogger<DaprEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(
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

        return daprClient.PublishEventAsync(pubSubName, topicName, payload, metadata, cancellationToken);
    }
}
