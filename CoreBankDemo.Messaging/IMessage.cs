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

    /// <summary>
    /// Claim priority within a partition. Claims are ordered by this value
    /// descending, then by arrival, so a higher-priority row is dispatched
    /// before every lower-priority row still queued in its partition -- and
    /// never before an earlier row of its own priority. The instant payment
    /// rail uses <see cref="MessageConstants.Priority.Instant"/> so that an
    /// SCT Inst never waits behind batch (SCT) work; everything else is
    /// <see cref="MessageConstants.Priority.Standard"/>.
    /// </summary>
    int Priority { get; set; }

    /// <summary>
    /// While set and in the future, the background batch claim leaves this
    /// row alone; the ordered inline claim ignores it. The instant rail sets
    /// it to the row's creation time plus its budget, so a fresh SCT Inst is
    /// settled by the request that stored it rather than being snatched by
    /// the next 200 ms poll tick -- which, once priority made instant rows
    /// first in line, the poller otherwise won almost every time. If the
    /// inline attempt gives up or dies, the hold lapses and the batch rail
    /// takes over exactly as before. <see langword="null"/> means no hold.
    /// </summary>
    DateTime? HoldUntil { get; set; }
}
