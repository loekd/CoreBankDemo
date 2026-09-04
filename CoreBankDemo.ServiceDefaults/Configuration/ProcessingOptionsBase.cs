using System.ComponentModel.DataAnnotations;

namespace CoreBankDemo.ServiceDefaults.Configuration;

/// <summary>
/// Shared, DataAnnotations-validated settings for the Inbox/Outbox/
/// MessagingOutbox processing options. Bound via
/// <c>AddOptions&lt;T&gt;().BindConfiguration(...).ValidateDataAnnotations()
/// .ValidateOnStart()</c> so every violation is reported together at startup,
/// not just the first.
/// <para>
/// Deliberately carries no <c>LockRenewIntervalSeconds</c> member: partition
/// locks are expiry-based and never renewed (AD-7). The brownfield original
/// bound and validated that member but nothing ever read it (ruling A4) — it
/// must not exist anywhere in the rebuild. See
/// <c>DeadOptionMembersTests</c> for the reflection guard.
/// </para>
/// </summary>
public abstract record ProcessingOptionsBase
{
    /// <summary>
    /// Number of partitions fanned out over on each processing tick. Defaults
    /// to 4, matching AD-4's documented system-wide partition count (ruling
    /// A3: the brownfield original misbound this to 2 in config).
    /// </summary>
    [Required]
    [Range(1, 100, ErrorMessage = "PartitionCount must be between 1 and 100")]
    public int PartitionCount { get; init; } = 4;

    /// <summary>Seconds a per-partition distributed lock is held before it expires.</summary>
    [Required]
    [Range(1, 300, ErrorMessage = "LockExpirySeconds must be between 1 and 300")]
    public int LockExpirySeconds { get; init; }

    /// <summary>Delay, in milliseconds, between the end of one poll tick and the start of the next.</summary>
    [Required]
    [Range(100, 300_000, ErrorMessage = "PollingIntervalMs must be between 100 and 300000")]
    public int PollingIntervalMs { get; init; } = 5_000;
}
