namespace CoreBankDemo.Messaging;

/// <summary>
/// Primitive-typed settings <see cref="OutboxProcessorBase{TMessage}"/> reads
/// each tick: partition fan-out width, per-partition lock hold duration, and
/// the delay between ticks. Defined locally in story 2.4 rather than
/// referencing the old <c>CoreBankDemo.ServiceDefaults</c>
/// <c>ProcessingOptionsBase</c>-derived types, which epic 3 demolishes when it
/// rebuilds the locking seam — a new kernel type stays decoupled from a type
/// about to disappear (spec-2-4 "Ask First" resolution). Deliberately does not
/// carry a <c>LockRenewIntervalSeconds</c> member — AD-7 rules that member
/// dead (locks are expiry-based, no renewal) and it must not exist anywhere in
/// the rebuild. Options validation (e.g. rejecting a non-positive
/// <see cref="PartitionCount"/>) is out of scope here — epic 3 wires up
/// validation for the options types it introduces.
/// </summary>
public sealed record OutboxProcessorOptions
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
    /// <see cref="OutboxProcessorBase{TMessage}"/>'s poll loop and throw
    /// there, crashing the whole <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
    /// instead of degrading — failing fast here, at construction, is simpler
    /// than clamping defensively on every tick.
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
