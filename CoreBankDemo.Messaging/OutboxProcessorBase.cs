using System.Diagnostics;
using CoreBankDemo.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoreBankDemo.Messaging;

/// <summary>
/// The single poll/lock/dispatch loop every outbox in the system inherits
/// (story 2.4; AD-3). The legacy defect this exists to fix (ADR A2) was
/// <c>MessagingOutboxProcessor</c> bypassing a shared base entirely and
/// re-implementing its own loop with an inline Dapr-publish call — this base
/// owns the loop, and delivery is a pluggable port
/// (<see cref="IOutboxDeliveryStrategy{TMessage}"/>) so a concrete transport
/// never has a reason to reimplement any of polling, partition fan-out,
/// locking, claiming, or retry classification.
///
/// <para>
/// Depends only on the four seams needed to run and test the loop —
/// <see cref="IOutboxMessageStore{TMessage}"/>, <see cref="IDistributedLockService"/>,
/// <see cref="IOutboxDeliveryStrategy{TMessage}"/>, and a ctor-injected
/// <see cref="ActivitySource"/> (never <c>new</c>'d here — the
/// <c>observability</c> skill and AD-8 require a registered source) — plus
/// <see cref="TimeProvider"/> and <see cref="ILogger"/>. Never a concrete
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/>: that is exactly what
/// <see cref="IOutboxMessageStore{TMessage}"/> exists to keep out of this
/// class, so it stays Moq-testable (AD-2/AD-9).
/// </para>
///
/// <para>
/// Success/failure classification is decided here, never by the strategy
/// (AD-11): the strategy returning normally means
/// <see cref="IOutboxMessageStore{TMessage}.MarkAsCompletedAsync"/>; the
/// strategy throwing means
/// <see cref="IOutboxMessageStore{TMessage}.MarkAsFailedWithRetryAsync"/> with
/// the exception's message — except a cancellation raised because
/// <paramref name="stoppingToken"/>-derived tokens were actually cancelled,
/// which is not a delivery failure and is left for the message to be picked
/// up again on a future tick (see <see cref="ProcessMessageAsync"/>).
/// Delivery and completion-persistence are classified independently of each
/// other: a <see cref="IOutboxMessageStore{TMessage}.MarkAsCompletedAsync"/>
/// failure AFTER a successful delivery is never misreported as a delivery
/// failure and never calls <c>MarkAsFailedWithRetryAsync</c> — see
/// <see cref="ProcessMessageAsync"/>.
/// </para>
/// </summary>
public abstract class OutboxProcessorBase<TMessage> : BackgroundService
    where TMessage : class, IOutboxMessage
{
    private readonly IOutboxMessageStore<TMessage> _store;
    private readonly IDistributedLockService _lockService;
    private readonly IOutboxDeliveryStrategy<TMessage> _deliveryStrategy;
    private readonly ActivitySource _activitySource;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly OutboxProcessorOptions _options;

    protected OutboxProcessorBase(
        IOutboxMessageStore<TMessage> store,
        IDistributedLockService lockService,
        IOutboxDeliveryStrategy<TMessage> deliveryStrategy,
        ActivitySource activitySource,
        TimeProvider timeProvider,
        ILogger logger,
        OutboxProcessorOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _lockService = lockService ?? throw new ArgumentNullException(nameof(lockService));
        _deliveryStrategy = deliveryStrategy ?? throw new ArgumentNullException(nameof(deliveryStrategy));
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new OutboxProcessorOptions();
    }

    /// <summary>
    /// Per-store lock-name namespace (e.g. <c>payments-outbox</c>); combined
    /// with a partition id to form each tick's per-partition lock name
    /// (<c>&lt;prefix&gt;-partition-&lt;id&gt;</c>).
    /// </summary>
    protected abstract string LockNamePrefix { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunTickAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(_options.PollingInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Host is stopping — the while condition above ends the loop.
            }
        }
    }

    /// <summary>
    /// Executes exactly one tick: every partition, in parallel, each under its
    /// own lock. Any exception escaping partition-level lock/store work (i.e.
    /// not from a single message's delivery — see <see cref="ProcessMessageAsync"/>
    /// for that) is logged and swallowed here, never rethrown, so a bad tick
    /// never breaks <see cref="ExecuteAsync"/>'s loop and the next tick is
    /// always scheduled.
    /// </summary>
    /// <remarks>
    /// Internal (rather than private) specifically so tests can invoke a
    /// single tick directly — see the test-seam note on
    /// <c>CoreBankDemo.Messaging.Tests.OutboxProcessorBaseTests</c> — without
    /// running the full <see cref="BackgroundService"/> host lifecycle.
    /// </remarks>
    internal async Task RunTickAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ProcessPartitionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing outbox partitions for lock prefix {LockNamePrefix}", LockNamePrefix);
        }
    }

    private async Task ProcessPartitionsAsync(CancellationToken cancellationToken)
    {
        var tasks = Enumerable.Range(0, _options.PartitionCount)
            .Select(partitionId => ProcessPartitionUnderLockAsync(partitionId, cancellationToken))
            .ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private Task ProcessPartitionUnderLockAsync(int partitionId, CancellationToken cancellationToken)
    {
        var lockName = $"{LockNamePrefix}-partition-{partitionId}";

        // ExecuteWithLockAsync returning false (lock not acquired) is a
        // normal, silent skip — not a failure — so its result is intentionally
        // discarded here.
        return _lockService.ExecuteWithLockAsync(
            lockName,
            _options.LockExpirySeconds,
            lockedCancellationToken => ProcessPartitionAsync(partitionId, lockedCancellationToken),
            cancellationToken);
    }

    private async Task ProcessPartitionAsync(int partitionId, CancellationToken cancellationToken)
    {
        var claimed = await _store
            .ClaimBatchForPartitionAsync(partitionId, MessageConstants.Defaults.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        // Defensive: a misbehaving IOutboxMessageStore implementation
        // returning null must not NRE its way into a masked, generic
        // tick-level "Error processing outbox partitions" log line — treat it
        // the same as an empty batch.
        if (claimed is null)
        {
            return;
        }

        // Sequential, oldest-first, per the batch's own ordering — preserves
        // per-key ordering within a partition (AD-4).
        foreach (var message in claimed)
        {
            await ProcessMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Delivers <paramref name="message"/> and then persists its completion —
    /// in two separate try/catch scopes, deliberately, never one shared
    /// <c>try</c> around both calls (that was the pre-fix defect this
    /// docstring exists to prevent regressing). A single shared
    /// <c>try</c>/<c>catch</c> cannot tell "delivery threw" apart from
    /// "delivery succeeded but <see cref="IOutboxMessageStore{TMessage}.MarkAsCompletedAsync"/>
    /// then threw" — e.g. a <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
    /// racing a concurrent stale-claim reclaim. Misclassifying the latter as a
    /// delivery failure would log a false "delivery failed" and call
    /// <see cref="IOutboxMessageStore{TMessage}.MarkAsFailedWithRetryAsync"/>,
    /// which flips an already-delivered message back to <c>Pending</c> and
    /// burns a <c>RetryCount</c> for a bookkeeping failure that has nothing to
    /// do with delivery — causing a redelivery of a message that already
    /// succeeded, and risking the message going terminally <c>Failed</c> purely
    /// from repeated completion-persistence hiccups. That violates AD-11's
    /// exactly-once delivery-OUTCOME contract (delivery may be re-attempted;
    /// what must never happen is reporting the wrong reason a message didn't
    /// reach <c>Completed</c>).
    /// </summary>
    private async Task ProcessMessageAsync(TMessage message, CancellationToken cancellationToken)
    {
        using var activity = StartDeliveryActivity(message);

        try
        {
            await _deliveryStrategy.DeliverAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation, not a delivery failure: the message is left exactly
            // as claiming left it (Processing) — not completed, not
            // retry-counted — and picked up again by a future claim (fresh, or
            // via stale-claim reclaim). Propagates out of this partition's
            // dispatch loop so no further messages in this batch are attempted
            // once cancellation has been observed ("stops promptly").
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Outbox delivery failed for message {MessageId} (IdempotencyKey={IdempotencyKey}, PartitionId={PartitionId})",
                message.Id, message.IdempotencyKey, message.PartitionId);
            await _store.MarkAsFailedWithRetryAsync(message, ex.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await _store.MarkAsCompletedAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Same rationale as above: not a delivery failure (delivery
            // already succeeded), so leave the message exactly as it is and
            // stop promptly rather than attempting anything further.
            throw;
        }
        catch (Exception ex)
        {
            // Delivery already succeeded — this is purely a completion-
            // bookkeeping failure, not a delivery failure, so it is logged
            // distinctly and MUST NOT call MarkAsFailedWithRetryAsync: doing so
            // would burn a RetryCount and flip an already-delivered message
            // back to Pending for immediate redelivery, misrepresenting what
            // actually failed. The message is left in its current claimed
            // (Processing) state; it will be naturally reclaimed once its
            // claim goes stale (story 2.3's ProcessingTimeout mechanism), and
            // redelivery at that point is a safe, already-designed-for outcome
            // because downstream receivers are idempotent.
            _logger.LogWarning(
                ex,
                "Failed to persist completion for message {MessageId} (IdempotencyKey={IdempotencyKey}, PartitionId={PartitionId}) after successful delivery; it will be redelivered once its claim goes stale",
                message.Id, message.IdempotencyKey, message.PartitionId);
        }
    }

    /// <summary>
    /// Starts the delivery span, restoring the stored <see cref="IMessage.TraceParent"/>/
    /// <see cref="IMessage.TraceState"/> as its parent when present and
    /// parseable (AD-8) — an outbox message being delivered is genuinely
    /// producing work on behalf of the trace that originally created it, hence
    /// <see cref="ActivityKind.Producer"/>. Tags always include
    /// <c>IdempotencyKey</c> and <c>PartitionId</c> per this story's
    /// boundaries (the legacy processor omitted <c>PartitionId</c>).
    /// </summary>
    private Activity? StartDeliveryActivity(TMessage message)
    {
        var hasParent = !string.IsNullOrWhiteSpace(message.TraceParent)
            && ActivityContext.TryParse(message.TraceParent, message.TraceState, out var parentContext);

        var activity = hasParent
            ? _activitySource.StartActivity("ProcessOutboxMessage", ActivityKind.Producer, parentContext)
            : _activitySource.StartActivity("ProcessOutboxMessage", ActivityKind.Producer);

        activity?.SetTag("IdempotencyKey", message.IdempotencyKey);
        activity?.SetTag("PartitionId", message.PartitionId);
        activity?.SetTag(
            "queue_duration_ms",
            (_timeProvider.GetUtcNow().UtcDateTime - message.CreatedAt).TotalMilliseconds);

        return activity;
    }
}
