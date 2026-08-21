namespace CoreBankDemo.Messaging;

/// <summary>
/// Common contract for inbox and outbox pattern messages.
/// </summary>
public interface IMessage
{
    /// <summary>Row identity.</summary>
    Guid Id { get; set; }

    /// <summary>Partition assignment (AD-4): derived via <c>PartitionHelper.GetPartitionId</c> from the message's dedupe identity (see the derived interfaces) and the configured partition count.</summary>
    int PartitionId { get; set; }

    /// <summary>Transport state; values come from <see cref="MessageConstants.Status"/> only (AD-11).</summary>
    string Status { get; set; }

    /// <summary>When the message reached a terminal successful state (UTC).</summary>
    DateTime? ProcessedAt { get; set; }

    /// <summary>Transport delivery attempts so far; terminal Failed at <see cref="MessageConstants.Defaults.MaxRetryCount"/>.</summary>
    int RetryCount { get; set; }

    /// <summary>Last transport error observed, if any.</summary>
    string? LastError { get; set; }

    /// <summary>W3C traceparent captured at enqueue; restored as span parent during processing (AD-8).</summary>
    string? TraceParent { get; set; }

    /// <summary>W3C tracestate captured at enqueue (AD-8).</summary>
    string? TraceState { get; set; }
}
