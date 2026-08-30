using System.Diagnostics;
using CoreBankDemo.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoreBankDemo.Messaging;

/// <summary>
/// The single poll/lock/dispatch loop every inbox in the system inherits
/// (story 2.5; AD-3), mirroring <see cref="OutboxProcessorBase{TMessage}"/>'s
/// loop shape exactly (story 2.4's direct pattern source) — the legacy inbox
/// never claimed to <c>Processing</c> and never scoped a handler per message
/// (FR-19; AD-3), and this base fixes both.
///
/// <para>
/// Depends only on the seams needed to run and test the loop —
/// <see cref="IDistributedLockService"/>, <see cref="IServiceScopeFactory"/>,
/// and a ctor-injected <see cref="ActivitySource"/> (never <c>new</c>'d here
/// — the <c>observability</c> skill and AD-8 require a registered source) —
/// plus <see cref="TimeProvider"/> and <see cref="ILogger"/>. Never a
/// concrete <see cref="Microsoft.EntityFrameworkCore.DbContext"/> and never a
/// ctor-injected <see cref="IInboxMessageStore{TMessage}"/> or handler
/// instance: unlike <see cref="OutboxProcessorBase{TMessage}"/>'s
/// ctor-injected singleton <see cref="IOutboxDeliveryStrategy{TMessage}"/>,
/// this base is itself a singleton hosted service (registered once for the
/// process lifetime), so ctor-injecting a scoped
/// <see cref="IInboxMessageStore{TMessage}"/> would create a captive
/// dependency — the container would resolve one scoped store instance at
/// startup and hold it captive for the singleton's entire lifetime, defeating
/// the store's scoped registration (e.g. a scoped
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/> would never be
/// recycled per partition). Instead, <see cref="ProcessPartitionAsync"/>
/// resolves a fresh <see cref="IInboxMessageStore{TMessage}"/> from its own
/// per-partition <see cref="IServiceScopeFactory"/>-created DI scope for that
/// partition's claim/complete/retry calls — mirroring the existing
/// per-message handler scope below, just at partition granularity — and a
/// fresh <see cref="IInboxMessageHandler{TMessage}"/> is still resolved per
/// message from its own fresh scope, so each message gets independent scoped
/// dependencies (e.g. a fresh <see cref="Microsoft.EntityFrameworkCore.DbContext"/>
/// for whatever the handler does), and this base stays Moq-testable
/// (AD-2/AD-9).
/// </para>
///
/// <para>
/// Success/failure classification is decided here, never by the handler
/// (AD-11): the handler returning normally means
/// <see cref="IInboxMessageStore{TMessage}.MarkAsCompletedAsync"/>; the
/// handler throwing means
/// <see cref="IInboxMessageStore{TMessage}.MarkAsFailedWithRetryAsync"/> with
/// the exception's message — except a cancellation raised because
/// <paramref name="stoppingToken"/>-derived tokens were actually cancelled,
/// which is not a handler failure and is left for the message to be picked
/// up again on a future tick (see <see cref="ProcessMessageAsync"/>).
/// Handling and completion-persistence are classified independently of each
/// other: a <see cref="IInboxMessageStore{TMessage}.MarkAsCompletedAsync"/>
/// failure AFTER a successful handler call is never misreported as a handler
/// failure and never calls <c>MarkAsFailedWithRetryAsync</c> — see
/// <see cref="ProcessMessageAsync"/> (the same separated-try/catch fix story
/// 2.4 applied, mirrored here).
/// </para>
/// </summary>
public abstract class InboxProcessorBase<TMessage> : BackgroundService
    where TMessage : class, IInboxMessage
{
    private readonly IDistributedLockService _lockService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ActivitySource _activitySource;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly InboxProcessorOptions _options;

    protected InboxProcessorBase(
        IDistributedLockService lockService,
        IServiceScopeFactory scopeFactory,
        ActivitySource activitySource,
        TimeProvider timeProvider,
        ILogger logger,
        InboxProcessorOptions? options = null)
    {
        _lockService = lockService ?? throw new ArgumentNullException(nameof(lockService));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new InboxProcessorOptions();
    }

    /// <summary>
    /// Per-store lock-name namespace (e.g. <c>payments-inbox</c>); combined
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
    /// not from a single message's handling — see <see cref="ProcessMessageAsync"/>
    /// for that) is logged and swallowed here, never rethrown, so a bad tick
    /// never breaks <see cref="ExecuteAsync"/>'s loop and the next tick is
    /// always scheduled.
    /// </summary>
    /// <remarks>
    /// Internal (rather than private) specifically so tests can invoke a
    /// single tick directly — see the test-seam note on
    /// <c>CoreBankDemo.Messaging.Tests.InboxProcessorBaseTests</c> — without
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
            _logger.LogError(ex, "Error processing inbox partitions for lock prefix {LockNamePrefix}", LockNamePrefix);
        }
    }

    private async Task ProcessPartitionsAsync(CancellationToken cancellationToken)
    {
        var tasks = Enumerable.Range(0, _options.PartitionCount)
            .Select(partitionId => ProcessPartitionUnderLockAsync(partitionId, cancellationToken))
            .ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one partition under its lock, isolated from every other
    /// partition's fan-out task. Deliberately wraps the
    /// <see cref="IDistributedLockService.ExecuteWithLockAsync"/> call itself
    /// in try/catch — not just relying on <see cref="RunTickAsync"/>'s
    /// tick-level catch — for two reasons: (1) the story 2.6 I/O matrix
    /// requires a lock-service exception be "caught at partition level" so
    /// the other partitions demonstrably still run rather than merely
    /// racing to finish before <see cref="Task.WhenAll(Task[])"/> observes
    /// the fault; (2) a lock-service implementation is free to throw
    /// <em>synchronously</em>, before ever returning a <see cref="Task"/>
    /// (e.g. eager argument validation) — since <see cref="ProcessPartitionsAsync"/>
    /// builds its fan-out via an eager <c>Select(...).ToArray()</c>, a
    /// synchronous throw from one partition's call would otherwise abort
    /// that enumeration outright and silently skip every partition after it.
    /// Making this method <c>async</c> guarantees such a throw is always
    /// captured into the returned <see cref="Task"/> instead, so every
    /// partition is always attempted regardless of how an earlier one fails.
    /// </summary>
    private async Task ProcessPartitionUnderLockAsync(int partitionId, CancellationToken cancellationToken)
    {
        var lockName = $"{LockNamePrefix}-partition-{partitionId}";

        try
        {
            // ExecuteWithLockAsync returning false (lock not acquired) is a
            // normal, silent skip — not a failure — so its result is
            // intentionally discarded here.
            await _lockService.ExecuteWithLockAsync(
                lockName,
                _options.LockExpirySeconds,
                lockedCancellationToken => ProcessPartitionAsync(partitionId, lockedCancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation, not a lock-service failure: an OperationCanceledException
            // reaching this catch can legitimately be ProcessMessageAsync's own
            // deliberate rethrow (see its catch (OperationCanceledException) when
            // (...) { throw; }) propagating through ExecuteWithLockAsync — either
            // because the ambient stoppingToken fired (normal host shutdown) or
            // because the lock service supplied the workload its own token (e.g.
            // a real IDistributedLockService's 5/6-lock-lifetime cutoff, epic 3),
            // which this method never observes directly and so cannot filter on
            // via the ambient cancellationToken alone. Either way this is ordinary
            // cancellation, not a distributed-lock backend failure, so it must not
            // be logged as "Lock service failed" — doing so would misdirect
            // on-call diagnosis during a real incident. Rethrown unconditionally
            // so the tick-level catch still swallows it, same as before.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Lock service failed for inbox partition {PartitionId} (lock {LockName})",
                partitionId, lockName);
        }
    }

    /// <summary>
    /// Resolves <see cref="IInboxMessageStore{TMessage}"/> from a fresh
    /// per-partition <see cref="IServiceScopeFactory"/>-created DI scope,
    /// disposed once this partition's claim/dispatch/complete/retry work
    /// returns or throws — never a ctor-injected singleton field (see the
    /// class-level remarks on the captive-dependency defect this avoids) —
    /// then claims and dispatches this partition's batch through that same
    /// store instance for the whole partition, mirroring the existing
    /// per-message handler scope below just at partition granularity.
    /// </summary>
    private async Task ProcessPartitionAsync(int partitionId, CancellationToken cancellationToken)
    {
        using var partitionScope = _scopeFactory.CreateScope();
        var store = partitionScope.ServiceProvider.GetRequiredService<IInboxMessageStore<TMessage>>();

        var claimed = await store
            .ClaimBatchForPartitionAsync(partitionId, MessageConstants.Defaults.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        // Defensive: a misbehaving IInboxMessageStore implementation
        // returning null must not NRE its way into a masked, generic
        // tick-level "Error processing inbox partitions" log line — treat it
        // the same as an empty batch.
        if (claimed is null)
        {
            return;
        }

        // Sequential, oldest-first, per the batch's own ordering — preserves
        // per-key ordering within a partition (AD-4).
        foreach (var message in claimed)
        {
            // Defensive, mirroring the null-batch guard above: a misbehaving
            // IInboxMessageStore implementation returning a non-null list
            // that contains a null element must not NRE its way into a
            // masked, generic tick-level error log line — skip it and keep
            // processing the rest of the batch.
            if (message is null)
            {
                continue;
            }

            await ProcessMessageAsync(store, message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles <paramref name="message"/> — resolving its
    /// <see cref="IInboxMessageHandler{TMessage}"/> from a fresh
    /// <see cref="IServiceScopeFactory"/>-created DI scope, disposed once this
    /// call returns or throws — and then persists its completion via
    /// <paramref name="store"/> (resolved by the caller,
    /// <see cref="ProcessPartitionAsync"/>, from that partition's own scope —
    /// never a ctor-injected field here), in two separate try/catch scopes,
    /// deliberately, never one shared <c>try</c> around both (mirroring story
    /// 2.4's fixed defect for the outbox). A single shared
    /// <c>try</c>/<c>catch</c> cannot tell "handling threw" apart from
    /// "handling succeeded but
    /// <see cref="IInboxMessageStore{TMessage}.MarkAsCompletedAsync"/> then
    /// threw" — e.g. a <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
    /// racing a concurrent stale-claim reclaim. Misclassifying the latter as a
    /// handler failure would log a false "handling failed" and call
    /// <see cref="IInboxMessageStore{TMessage}.MarkAsFailedWithRetryAsync"/>,
    /// which flips an already-handled message back to <c>Pending</c> and
    /// burns a <c>RetryCount</c> for a bookkeeping failure that has nothing to
    /// do with handling — causing a redelivery of a message that already
    /// succeeded, and risking the message going terminally <c>Failed</c> purely
    /// from repeated completion-persistence hiccups. That violates AD-11's
    /// exactly-once handling-OUTCOME contract (handling may be re-attempted;
    /// what must never happen is reporting the wrong reason a message didn't
    /// reach <c>Completed</c>).
    /// </summary>
    private async Task ProcessMessageAsync(IInboxMessageStore<TMessage> store, TMessage message, CancellationToken cancellationToken)
    {
        using var activity = StartDispatchActivity(message);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<IInboxMessageHandler<TMessage>>();
            await handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation, not a handler failure: the message is left exactly
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
                "Inbox handling failed for message {MessageId} (IdempotencyKey={IdempotencyKey}, PartitionId={PartitionId})",
                message.Id, message.IdempotencyKey, message.PartitionId);

            try
            {
                await store.MarkAsFailedWithRetryAsync(message, ex.Message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Same rationale as the completion path: stop promptly rather
                // than attempting anything further once cancellation has been
                // observed.
                throw;
            }
            catch (Exception retryEx)
            {
                // Recording the retry is itself bookkeeping, separate from the
                // handler failure already logged above — e.g. a transient DB
                // conflict while persisting the retry. This must never escape
                // ProcessMessageAsync: doing so would abort the rest of this
                // partition's batch for the tick, leaving the remaining
                // claimed messages undispatched. The message is left in its
                // current claimed (Processing) state and will be naturally
                // reclaimed once its claim goes stale (story 2.3's
                // ProcessingTimeout mechanism).
                _logger.LogWarning(
                    retryEx,
                    "Failed to record retry for message {MessageId} (IdempotencyKey={IdempotencyKey}, PartitionId={PartitionId}) after a handler failure",
                    message.Id, message.IdempotencyKey, message.PartitionId);
            }

            return;
        }

        try
        {
            await store.MarkAsCompletedAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Same rationale as above: not a handler failure (handling
            // already succeeded), so leave the message exactly as it is and
            // stop promptly rather than attempting anything further.
            throw;
        }
        catch (Exception ex)
        {
            // Handling already succeeded — this is purely a completion-
            // bookkeeping failure, not a handler failure, so it is logged
            // distinctly and MUST NOT call MarkAsFailedWithRetryAsync: doing so
            // would burn a RetryCount and flip an already-handled message
            // back to Pending for immediate reprocessing, misrepresenting what
            // actually failed. The message is left in its current claimed
            // (Processing) state; it will be naturally reclaimed once its
            // claim goes stale (story 2.3's ProcessingTimeout mechanism), and
            // reprocessing at that point is a safe, already-designed-for
            // outcome because handlers are expected to be idempotent.
            _logger.LogWarning(
                ex,
                "Failed to persist completion for message {MessageId} (IdempotencyKey={IdempotencyKey}, PartitionId={PartitionId}) after successful handling; it will be reprocessed once its claim goes stale",
                message.Id, message.IdempotencyKey, message.PartitionId);
        }
    }

    /// <summary>
    /// Starts the dispatch span, restoring the stored <see cref="IMessage.TraceParent"/>/
    /// <see cref="IMessage.TraceState"/> as its parent when present and
    /// parseable (AD-8) — an inbox message being handled is genuinely
    /// consuming work handed off by the trace that originally sent it, hence
    /// <see cref="ActivityKind.Consumer"/>. Tags always include
    /// <c>IdempotencyKey</c> and <c>PartitionId</c> per this story's
    /// boundaries (the legacy processor omitted <c>PartitionId</c>).
    /// </summary>
    private Activity? StartDispatchActivity(TMessage message)
    {
        var hasParent = !string.IsNullOrWhiteSpace(message.TraceParent)
            && ActivityContext.TryParse(message.TraceParent, message.TraceState, out var parentContext);

        var activity = hasParent
            ? _activitySource.StartActivity("ProcessInboxMessage", ActivityKind.Consumer, parentContext)
            : _activitySource.StartActivity("ProcessInboxMessage", ActivityKind.Consumer);

        activity?.SetTag("IdempotencyKey", message.IdempotencyKey);
        activity?.SetTag("PartitionId", message.PartitionId);
        activity?.SetTag(
            "queue_duration_ms",
            (_timeProvider.GetUtcNow().UtcDateTime - message.ReceivedAt).TotalMilliseconds);

        return activity;
    }
}
