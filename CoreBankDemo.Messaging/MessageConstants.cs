namespace CoreBankDemo.Messaging;

/// <summary>
/// Constants for message processing across inbox and outbox patterns.
/// </summary>
public static class MessageConstants
{
    /// <summary>
    /// Message status values.
    /// </summary>
    public static class Status
    {
        public const string Pending = "Pending";
        public const string Processing = "Processing";
        public const string Completed = "Completed";
        public const string Failed = "Failed";
    }

    /// <summary>
    /// Default configuration values for message processing.
    /// </summary>
    public static class Defaults
    {
        /// <summary>
        /// Maximum number of retry attempts before giving up.
        /// </summary>
        public const int MaxRetryCount = 5;

        /// <summary>
        /// Number of messages to process in a single batch.
        /// </summary>
        public const int BatchSize = 10;

        /// <summary>
        /// Timeout after which a message in "Processing" status is considered stale.
        /// </summary>
        public static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Interval between polling for new messages to process.
        /// </summary>
        public static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);
    }
}
