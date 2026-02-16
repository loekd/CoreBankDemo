using System.ComponentModel.DataAnnotations;

namespace CoreBankDemo.ServiceDefaults.Configuration;

public record OutboxProcessingOptions
{
    public const string SectionName = "OutboxProcessing";

    [Required]
    [Range(1, 100, ErrorMessage = "PartitionCount must be between 1 and 100")]
    public int PartitionCount { get; init; }

    [Required]
    [Range(1, 300, ErrorMessage = "LockExpirySeconds must be between 1 and 300")]
    public int LockExpirySeconds { get; init; }

    [Required]
    [Range(1, 240, ErrorMessage = "LockRenewIntervalSeconds must be between 1 and 240")]
    public int LockRenewIntervalSeconds { get; init; }
}
