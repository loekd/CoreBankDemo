using System.ComponentModel.DataAnnotations;

namespace CoreBankDemo.ServiceDefaults.Configuration;

public record MessagingOutboxProcessingOptions : ProcessingOptionsBase
{
    public const string SectionName = "MessagingOutboxProcessing";

    [Required]
    [MinLength(1, ErrorMessage = "PubSubName is required")]
    public string PubSubName { get; init; } = "pubsub";

    [Required]
    [MinLength(1, ErrorMessage = "TopicName is required")]
    public string TopicName { get; init; } = "transaction-events";
}
