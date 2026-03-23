using System.ComponentModel.DataAnnotations;

namespace CoreBankDemo.ServiceDefaults.Configuration;

public record MessagingOutboxProcessingOptions
{
    public const string SectionName = "MessagingOutboxProcessing";

    [Required]
    [Range(1, 100, ErrorMessage = "PartitionCount must be between 1 and 100")]
    public int PartitionCount { get; init; }

    [Required]
    [Range(1, 300, ErrorMessage = "LockExpirySeconds must be between 1 and 300")]
    public int LockExpirySeconds { get; init; }

    [Required]
    [Range(1, 240, ErrorMessage = "LockRenewIntervalSeconds must be between 1 and 240")]
    public int LockRenewIntervalSeconds { get; init; }

    [Required]
    [Range(100, 300_000, ErrorMessage = "PollingIntervalMs must be between 100 and 300000")]
    public int PollingIntervalMs { get; init; } = 5_000;

    [Required]
    [MinLength(1, ErrorMessage = "PubSubName is required")]
    public string PubSubName { get; init; } = "pubsub";

    [Required]
    [MinLength(1, ErrorMessage = "TopicName is required")]
    public string TopicName { get; init; } = "transaction-events";
}
