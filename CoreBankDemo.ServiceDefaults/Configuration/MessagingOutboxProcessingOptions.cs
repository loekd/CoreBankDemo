using System.ComponentModel.DataAnnotations;

namespace CoreBankDemo.ServiceDefaults.Configuration;

/// <summary>
/// Subclass of <see cref="ProcessingOptionsBase"/> bound to the
/// <c>MessagingOutboxProcessing</c> configuration section. Adds the Dapr
/// pub/sub identifiers the messaging-outbox processor needs to publish
/// transaction events.
/// </summary>
public sealed record MessagingOutboxProcessingOptions : ProcessingOptionsBase
{
    public const string SectionName = "MessagingOutboxProcessing";

    /// <summary>Dapr pub/sub component name transaction events are published through.</summary>
    [Required]
    [MinLength(1, ErrorMessage = "PubSubName is required")]
    [RegularExpression(@".*\S.*", ErrorMessage = "PubSubName must not be whitespace-only")]
    public string PubSubName { get; init; } = "pubsub";

    /// <summary>Dapr topic name transaction events are published to.</summary>
    [Required]
    [MinLength(1, ErrorMessage = "TopicName is required")]
    [RegularExpression(@".*\S.*", ErrorMessage = "TopicName must not be whitespace-only")]
    public string TopicName { get; init; } = "transaction-events";
}
