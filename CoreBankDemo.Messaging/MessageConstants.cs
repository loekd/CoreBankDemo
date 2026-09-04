namespace CoreBankDemo.Messaging;

/// <summary>
/// The single home for message statuses and processing limits (consistency
/// convention: no status/limit literal exists outside this class). Values are
/// verbatim from the legacy kernel — existing rows carry these strings.
/// </summary>
public static class MessageConstants
{
    /// <summary>
    /// Message transport states (AD-11: transport states only — business
    /// rejection is a successfully processed message, never <see cref="Failed"/>).
    /// </summary>
    public static class Status
    {
        public const string Pending = "Pending";
        public const string Processing = "Processing";
        public const string Completed = "Completed";

        /// <summary>Terminal: transport gave up after <see cref="Defaults.MaxRetryCount"/>.</summary>
        public const string Failed = "Failed";
    }

    /// <summary>
    /// Claim priorities (<see cref="IMessage.Priority"/>). Higher is claimed
    /// first within a partition; ties fall back to arrival order.
    /// </summary>
    public static class Priority
    {
        /// <summary>The batch rail (SCT) and every event store: plain arrival order.</summary>
        public const int Standard = 0;

        /// <summary>The instant rail (SCT Inst): claimed ahead of any queued standard work.</summary>
        public const int Instant = 100;
    }

    /// <summary>
    /// Default configuration values for message processing.
    /// </summary>
    public static class Defaults
    {
        /// <summary>Maximum number of retry attempts before terminal <see cref="Status.Failed"/>.</summary>
        public const int MaxRetryCount = 5;

        /// <summary>Number of messages claimed in a single batch.</summary>
        public const int BatchSize = 10;

        /// <summary>Timeout after which a "Processing" row is considered stale and reclaimable.</summary>
        public static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);

        /// <summary>Interval between polling ticks.</summary>
        public static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);
    }
}
