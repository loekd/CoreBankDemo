namespace CoreBankDemo.Messaging;

/// <summary>
/// Primitive-typed settings <see cref="InboxProcessorBase{TMessage}"/> reads
/// each tick: partition fan-out width, per-partition lock hold duration, and
/// the delay between ticks. Mirrors <see cref="OutboxProcessorOptions"/>
/// exactly (story 2.4's direct pattern source), including its
/// <see cref="PollingInterval"/> fail-fast validation. Defined as its own type
/// rather than reusing <see cref="OutboxProcessorOptions"/> so inbox and
/// outbox processors stay decoupled from each other's option surface —
/// deliberately does not carry a <c>LockRenewIntervalSeconds</c> member (AD-7
/// rules that member dead; it must not exist anywhere in the rebuild).
/// Options validation beyond the <see cref="PollingInterval"/> guard is out of
/// scope here — epic 3 wires up validation for the options types it
/// introduces.
/// </summary>
public sealed record InboxProcessorOptions
{
    /// <summary>
    /// Number of partitions fanned out over on each tick. AD-4 validates the
    /// system-wide partition count at 4; this default matches it, but a
    /// concrete processor may override it via the constructor.
    /// </summary>
    public int PartitionCount { get; init; } = 4;

    /// <summary>
    /// Seconds a per-partition lock is held before it expires. AD-7: locks are
    /// expiry-based with no renewal.
    /// </summary>
    public int LockExpirySeconds { get; init; } = 30;

    /// <summary>Delay between the end of one tick and the start of the next.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The assigned value is not positive. Full options validation is out of
    /// scope here (epic 3's concern, per this type's class remarks) but a
    /// non-positive interval would otherwise reach
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> in
    /// <see cref="InboxProcessorBase{TMessage}"/>'s poll loop and throw
    /// there, crashing the whole <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
    /// instance instead of degrading — failing fast here, at construction, is
    /// simpler than clamping defensively on every tick.
    /// </exception>
    public TimeSpan PollingInterval
    {
        get => _pollingInterval;
        init
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(PollingInterval), value, "PollingInterval must be positive.");
            }

            _pollingInterval = value;
        }
    }

    private readonly TimeSpan _pollingInterval = MessageConstants.Defaults.PollingInterval;
}
