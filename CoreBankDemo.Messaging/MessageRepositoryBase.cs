using System.Reflection;
using CoreBankDemo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoreBankDemo.Messaging;

/// <summary>
/// Shared storage layer behind <see cref="InboxMessageRepositoryBase{TMessage,TDbContext}"/>
/// and <see cref="OutboxMessageRepositoryBase{TMessage,TDbContext}"/> (story 2.2):
/// race-safe <see cref="StoreIfNewAsync"/> (insert-then-catch, never
/// check-then-insert — AD-4) plus the entity-configuration hook each concrete
/// store uses to declare its dedupe unique index. Claiming, retry/poison
/// handling, and the processor-facing query methods described in the epic-2
/// legacy reference are added by later stories (2.3+) — this base intentionally
/// stops at the store.
/// </summary>
public abstract class MessageRepositoryBase<TMessage, TDbContext>
    where TMessage : class, IMessage
    where TDbContext : DbContext
{
    private readonly BusinessMetrics _businessMetrics;

    protected MessageRepositoryBase(TDbContext dbContext, TimeProvider timeProvider, BusinessMetrics businessMetrics)
    {
        DbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _businessMetrics = businessMetrics ?? throw new ArgumentNullException(nameof(businessMetrics));
    }

    protected TDbContext DbContext { get; }

    /// <summary>
    /// Injected per the <c>conventions</c> skill (never <c>DateTime.Now/UtcNow</c>
    /// directly). Not read by this base yet — reserved for the stale-claim and
    /// completion-timestamp logic later stories add on top of this store.
    /// </summary>
    protected TimeProvider TimeProvider { get; }

    /// <summary>The store's message table; named by the inbox/outbox-specific base.</summary>
    protected abstract DbSet<TMessage> Messages { get; }

    /// <summary>
    /// This store's stable business identity (story 6.5) — e.g.
    /// <see cref="BusinessMetrics.StoreName.CoreBankInbox"/> — supplied by the
    /// concrete leaf repository. Never derived from a CLR type name or this
    /// store's distributed-lock prefix (design notes): a lock prefix is an
    /// infrastructure contention scope, while this identity is the durable
    /// store a metric attribute reports.
    /// </summary>
    protected abstract BusinessMetrics.StoreName StoreName { get; }

    /// <summary>Whether this store is an inbox or an outbox (story 6.5) — fixed by the inbox/outbox-specific base, never by the leaf repository.</summary>
    protected abstract BusinessMetrics.StoreKind StoreKind { get; }

    /// <summary>
    /// Stores <paramref name="message"/> if no row with the same dedupe identity
    /// exists yet. Always inserts optimistically and relies on the store's
    /// dedupe unique index (see <see cref="ConfigureDedupeIndex"/>) to reject
    /// duplicates at the database — never a pre-check, which is racy under
    /// concurrent callers (AD-4).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this call inserted the row;
    /// <see langword="false"/> when a concurrent or prior call already owns the
    /// dedupe identity ("already exists" — no exception is thrown for this
    /// case). Any failed call — whether it lost the dedupe race or
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> failed for
    /// any other reason — detaches <paramref name="message"/> from the change
    /// tracker before returning/rethrowing so <see cref="DbContext"/> stays
    /// usable for further operations by the caller.
    /// </returns>
    /// <exception cref="DbUpdateException">
    /// The save failed for a reason other than a unique-constraint violation;
    /// propagates unchanged (after detaching <paramref name="message"/>).
    /// </exception>
    public virtual async Task<bool> StoreIfNewAsync(TMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        Messages.Add(message);
        try
        {
            await DbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _businessMetrics.RecordStoreOperation(StoreName, StoreKind, BusinessMetrics.StoreOperationOutcome.Added);
            return true;
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsUniqueViolation(ex))
        {
            DbContext.Entry(message).State = EntityState.Detached;
            _businessMetrics.RecordStoreOperation(StoreName, StoreKind, BusinessMetrics.StoreOperationOutcome.Duplicate);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation, not a store failure (story 6.5 boundaries): never
            // recorded as `failed` — the caller stopped, the store did not.
            DbContext.Entry(message).State = EntityState.Detached;
            throw;
        }
        catch
        {
            // Any other save failure (timeout, a different DbUpdateException,
            // ...) still leaves the entity tracked as Added unless we detach
            // it here — a stale tracked entity would corrupt every subsequent
            // SaveChangesAsync on this context. Recorded before the rethrow
            // (metric contract) so a caller that swallows/wraps this
            // exception still leaves the failure visible in metrics.
            DbContext.Entry(message).State = EntityState.Detached;
            _businessMetrics.RecordStoreOperation(StoreName, StoreKind, BusinessMetrics.StoreOperationOutcome.Failed);
            throw;
        }
    }

    /// <summary>
    /// Entity-configuration hook (AD-4): declares the store's dedupe unique
    /// index. Command stores pass the idempotency key alone; event stores pass
    /// the composite event identity (e.g. idempotency key + event type
    /// [+ account]) — one row per distinct combination is allowed, so distinct
    /// identities sharing a key still store independently. Call this from the
    /// concrete <typeparamref name="TDbContext"/>'s <c>OnModelCreating</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="dedupePropertyNames"/> is <see langword="null"/> or
    /// empty; names a property that is not a public instance property of
    /// <typeparamref name="TMessage"/>; or contains the same property name
    /// more than once.
    /// </exception>
    public static void ConfigureDedupeIndex(EntityTypeBuilder<TMessage> builder, params string[] dedupePropertyNames)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (dedupePropertyNames is null || dedupePropertyNames.Length == 0)
        {
            throw new ArgumentException("At least one dedupe property name is required.", nameof(dedupePropertyNames));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var propertyName in dedupePropertyNames)
        {
            if (typeof(TMessage).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance) is null)
            {
                throw new ArgumentException(
                    $"'{propertyName}' is not a public instance property of {typeof(TMessage).Name}.",
                    nameof(dedupePropertyNames));
            }

            if (!seen.Add(propertyName))
            {
                throw new ArgumentException(
                    $"Duplicate dedupe property name '{propertyName}'.",
                    nameof(dedupePropertyNames));
            }
        }

        builder.HasIndex(dedupePropertyNames).IsUnique();
    }

    /// <summary>
    /// Entity-configuration hook (story 2.3): marks <see cref="IMessage.Status"/>
    /// as an EF Core concurrency token so <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
    /// includes the row's last-known <c>Status</c> in its generated
    /// <c>UPDATE ... WHERE</c> clause. This is what makes
    /// <see cref="ClaimBatchForPartitionAsync"/> safe under concurrent callers:
    /// two callers that both load the same Pending row cannot both flip it to
    /// Processing — the loser's save affects zero rows and EF reports it as a
    /// <see cref="DbUpdateConcurrencyException"/> rather than a silent double
    /// claim. Call this from the concrete <typeparamref name="TDbContext"/>'s
    /// <c>OnModelCreating</c> alongside <see cref="ConfigureDedupeIndex"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static void ConfigureConcurrencyToken(EntityTypeBuilder<TMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Property(m => m.Status).IsConcurrencyToken();
    }

    /// <summary>
    /// Query for this store's claimable rows in <paramref name="partitionId"/>,
    /// ordered by <see cref="IMessage.Priority"/> descending and then oldest-first
    /// by the store's ordering timestamp (<c>ReceivedAt</c> for inbox,
    /// <c>CreatedAt</c> for outbox): rows that are <c>Pending</c>, or
    /// <c>Processing</c> rows whose ordering timestamp is older than
    /// <paramref name="staleThreshold"/> (stale-claim reclaim, AD-3), excluding
    /// poisoned rows (<c>RetryCount &gt;= MaxRetryCount</c>). Implemented by the
    /// inbox/outbox base — this base class does not know the concrete ordering
    /// timestamp property.
    /// </summary>
    /// <param name="partitionId">The partition to query.</param>
    /// <param name="staleThreshold">Processing rows older than this are reclaimable.</param>
    /// <param name="holdCutoff">
    /// When given, rows whose <see cref="IMessage.HoldUntil"/> is still after
    /// it are excluded (the background batch claim); <see langword="null"/>
    /// ignores holds (the ordered inline claim, which is what a hold is for).
    /// </param>
    protected abstract IQueryable<TMessage> GetClaimableMessagesQuery(int partitionId, DateTime staleThreshold, DateTime? holdCutoff);

    /// <summary>
    /// Sets <paramref name="message"/>'s ordering timestamp
    /// (<c>ReceivedAt</c>/<c>CreatedAt</c>) to <paramref name="claimedAt"/>.
    /// Called by <see cref="ClaimBatchForPartitionAsync"/> ONLY for rows that
    /// were already <c>Processing</c> before this claim call — i.e. a true
    /// stale-claim reclaim — never for rows claimed fresh from <c>Pending</c>
    /// (the story 2.3 fix for the legacy staleness-basis violation: the old
    /// kernel measured staleness from the row's creation/receipt time, so a
    /// message that merely took a while to be picked up looked "stale" the
    /// instant it was legitimately claimed). Stamping the ordering timestamp
    /// forward only on reclaim makes the next staleness check measure from when
    /// the row actually got stuck, not from when it first arrived, while still
    /// preserving a row's true arrival order (relative to every other row) the
    /// first time it is ever claimed — matching
    /// <see cref="IInboxMessage.ReceivedAt"/>'s documented dual role as
    /// "ordering timestamp for claims". Stamping forward on every claim
    /// (including fresh-from-Pending ones) would destroy a message's true
    /// arrival timestamp on its very first claim and, if it is later reclaimed
    /// after crashing, permanently lose its place in the arrival-order queue
    /// relative to messages that arrived later — violating the per-partition
    /// oldest-first FIFO guarantee (AD-4) across separate claim calls.
    /// </summary>
    protected abstract void SetOrderingTimestamp(TMessage message, DateTime claimedAt);

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> claimable rows (see
    /// <see cref="GetClaimableMessagesQuery"/>) in <paramref name="partitionId"/>,
    /// highest priority first and oldest first within a priority, atomically
    /// transitioning them to <c>Processing</c> — no row
    /// can be claimed by two concurrent callers (see
    /// <see cref="ConfigureConcurrencyToken"/>). A caller that loses the race for
    /// some of its candidate rows simply keeps whatever it did win; losing a row
    /// to a concurrent claimer is a normal outcome, never an exception. Only
    /// rows that were already <c>Processing</c> before this call (true
    /// stale-claim reclaims) get their ordering timestamp stamped forward — see
    /// <see cref="SetOrderingTimestamp"/> — so a row's true arrival order is
    /// preserved across its very first claim and survives being reclaimed later.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="partitionId"/> is negative, or <paramref name="batchSize"/> is not positive.
    /// </exception>
    public virtual async Task<IReadOnlyList<TMessage>> ClaimBatchForPartitionAsync(
        int partitionId, int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(partitionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var claimedAt = TimeProvider.GetUtcNow().UtcDateTime;
        var staleThreshold = claimedAt - MessageConstants.Defaults.ProcessingTimeout;

        var claimed = await GetClaimableMessagesQuery(partitionId, staleThreshold, holdCutoff: claimedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Captured once, before any mutation: only rows that were ALREADY
        // Processing before this call are true stale reclaims. Rows claimed
        // fresh from Pending must never have their ordering timestamp touched.
        // This must be captured outside the retry loop below — by the second
        // iteration every remaining row's in-memory Status has already been
        // flipped to Processing by the first iteration's mutation, so checking
        // message.Status inside the loop would misclassify fresh claims as
        // reclaims on retry.
        var wasAlreadyProcessing = claimed
            .Where(m => m.Status == MessageConstants.Status.Processing)
            .Select(m => m.Id)
            .ToHashSet();

        while (claimed.Count > 0)
        {
            foreach (var message in claimed)
            {
                message.Status = MessageConstants.Status.Processing;
                if (wasAlreadyProcessing.Contains(message.Id))
                {
                    SetOrderingTimestamp(message, claimedAt);
                }
            }

            try
            {
                await ExecuteInTransactionAsync(
                    () => DbContext.SaveChangesAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                return claimed;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Lost the claim race for these specific rows — a concurrent
                // caller already flipped them to Processing first. Drop them
                // (detaching so the tracker doesn't try to resend a stale
                // update) and keep whatever this call still legitimately owns.
                // Guard the cast (ex.Entries is EF's generic change-tracker
                // surface, not typed to TMessage) and don't assume Remove finds
                // a match — a defensive no-op is correct either way rather than
                // risking an InvalidCastException here.
                foreach (var entry in ex.Entries)
                {
                    if (entry.Entity is TMessage message)
                    {
                        claimed.Remove(message);
                    }

                    entry.State = EntityState.Detached;
                }
            }
        }

        return claimed;
    }

    /// <summary>
    /// Transport-failure retry/poison transition (AD-11: the ONLY path that
    /// ever writes terminal <see cref="MessageConstants.Status.Failed"/> —
    /// business rejections never call this method; they store a Completed row
    /// with a cached failure payload instead, per stories 4.x). Increments
    /// <paramref name="message"/>'s <c>RetryCount</c> and sets
    /// <paramref name="errorMessage"/> as <c>LastError</c>; below
    /// <see cref="MessageConstants.Defaults.MaxRetryCount"/> the row goes back
    /// to <c>Pending</c> for another attempt, at the limit it becomes terminal
    /// <c>Failed</c>. A <paramref name="message"/> that is already terminal
    /// <c>Failed</c> is left untouched (no-op) — otherwise a repeat call on the
    /// same poisoned row would keep incrementing <c>RetryCount</c> past
    /// <see cref="MessageConstants.Defaults.MaxRetryCount"/> forever. A
    /// <paramref name="message"/> not currently tracked by this repository's
    /// <see cref="DbContext"/> (e.g. loaded via a different context instance)
    /// is attached first — otherwise <c>SaveChangesAsync</c> would silently
    /// persist nothing. <c>Status</c> is a concurrency token (see
    /// <see cref="ConfigureConcurrencyToken"/>); a conflicting concurrent
    /// change (e.g. a concurrent claim) is retried exactly once against the
    /// row's current database values before giving up.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> or <paramref name="errorMessage"/> is <see langword="null"/>.</exception>
    /// <exception cref="DbUpdateConcurrencyException">
    /// The retried save still conflicted with a second concurrent change to
    /// <paramref name="message"/>'s row; propagates unchanged.
    /// </exception>
    public virtual async Task<MessageTransitionOutcome> MarkAsFailedWithRetryAsync(
        TMessage message, string errorMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(errorMessage);

        if (message.Status == MessageConstants.Status.Failed)
        {
            // Already terminal — a repeat report of failure for a row that has
            // already been given up on must be a no-op, not another
            // RetryCount increment past MaxRetryCount.
            return MessageTransitionOutcome.AlreadyTerminal;
        }

        if (DbContext.Entry(message).State == EntityState.Detached)
        {
            // A message obtained from a different DbContext instance has no
            // tracked entry here; without attaching it, SaveChangesAsync below
            // would have nothing to write.
            DbContext.Attach(message);
        }

        ApplyFailureTransition(message, errorMessage);

        try
        {
            await DbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Lost the race against a concurrent change to this row's Status
            // (e.g. a concurrent claim). Reload the row's current database
            // values — this overwrites our speculative, now-stale mutation —
            // and retry the transition exactly once against them. A second
            // conflict is treated as a genuine anomaly and propagates.
            await DbContext.Entry(message).ReloadAsync(cancellationToken).ConfigureAwait(false);

            if (message.Status == MessageConstants.Status.Failed)
            {
                // The concurrent change already drove this row to terminal
                // Failed (e.g. another caller's retry hit MaxRetryCount) —
                // nothing further for this call to do.
                return MessageTransitionOutcome.AlreadyTerminal;
            }

            ApplyFailureTransition(message, errorMessage);
            await DbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return MessageTransitionOutcome.Applied;
    }

    private static void ApplyFailureTransition(TMessage message, string errorMessage)
    {
        message.RetryCount += 1;
        message.LastError = errorMessage;
        message.Status = message.RetryCount >= MessageConstants.Defaults.MaxRetryCount
            ? MessageConstants.Status.Failed
            : MessageConstants.Status.Pending;
    }

    /// <summary>
    /// Transport-success transition (story 2.4; AD-11): the ONLY path that
    /// writes <see cref="MessageConstants.Status.Completed"/> from a delivery
    /// strategy's success — never called for a business rejection cached as a
    /// Completed row by application code elsewhere, but that's still the same
    /// terminal state. Sets <c>Status = Completed</c> and stamps
    /// <c>ProcessedAt</c> from <see cref="TimeProvider"/>. Mirrors
    /// <see cref="MarkAsFailedWithRetryAsync"/>'s detach/attach handling (a
    /// <paramref name="message"/> loaded via a different
    /// <see cref="DbContext"/> instance is attached before saving), its
    /// single-retry response to <see cref="DbUpdateConcurrencyException"/>
    /// (<c>Status</c> is a concurrency token — see
    /// <see cref="ConfigureConcurrencyToken"/> — so a conflicting concurrent
    /// change is retried exactly once against the row's current database
    /// values before giving up), AND its terminal-status guard: a
    /// <paramref name="message"/> whose current <c>Status</c> is already
    /// terminal (<c>Completed</c> or <c>Failed</c>) is left untouched
    /// (no-op) rather than re-stamping <c>ProcessedAt</c> or, worse, reviving
    /// a row a concurrent caller already drove to terminal <c>Failed</c> (e.g.
    /// its retries were exhausted) back to <c>Completed</c>. Checked both
    /// before the first save attempt and again after a reload in the
    /// concurrency-conflict retry branch, since a concurrent change observed
    /// only via that reload could itself have been the one that made the row
    /// terminal.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="DbUpdateConcurrencyException">
    /// The retried save still conflicted with a second concurrent change to
    /// <paramref name="message"/>'s row; propagates unchanged.
    /// </exception>
    public virtual async Task<MessageTransitionOutcome> MarkAsCompletedAsync(
        TMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (IsTerminal(message))
        {
            // Already Completed or already Failed — a repeat completion
            // report for a row that is already in a terminal state must be a
            // no-op, never re-stamping ProcessedAt or reviving a row that a
            // concurrent caller already drove to terminal Failed.
            return MessageTransitionOutcome.AlreadyTerminal;
        }

        if (DbContext.Entry(message).State == EntityState.Detached)
        {
            DbContext.Attach(message);
        }

        ApplyCompletionTransition(message);

        try
        {
            await DbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            await DbContext.Entry(message).ReloadAsync(cancellationToken).ConfigureAwait(false);

            if (IsTerminal(message))
            {
                // The concurrent change already drove this row to a terminal
                // state (Completed by another caller, or Failed via retry
                // exhaustion) — nothing further for this call to do.
                return MessageTransitionOutcome.AlreadyTerminal;
            }

            ApplyCompletionTransition(message);
            await DbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return MessageTransitionOutcome.Applied;
    }

    private static bool IsTerminal(TMessage message) =>
        message.Status is MessageConstants.Status.Completed or MessageConstants.Status.Failed;

    private void ApplyCompletionTransition(TMessage message)
    {
        message.Status = MessageConstants.Status.Completed;
        message.ProcessedAt = TimeProvider.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// Claims exactly the row identified by <paramref name="id"/> if it is
    /// currently <c>Pending</c> (spec: add-instant-payment-rail's inline
    /// delivery path) — never a batch, never partition-scoped: the caller
    /// already knows which row it wants to deliver inline, right after
    /// storing it. Reuses the same concurrency-token transition
    /// <see cref="ClaimBatchForPartitionAsync"/> uses: <c>Status</c> is an EF
    /// Core concurrency token (<see cref="ConfigureConcurrencyToken"/>), so a
    /// concurrent background batch claim racing this call cannot both win —
    /// one save succeeds, the other observes
    /// <see cref="DbUpdateConcurrencyException"/> and reports no claim. A row
    /// that is not currently <c>Pending</c> — already <c>Processing</c> under
    /// a live claim, or already terminal (<c>Completed</c>/<c>Failed</c>) —
    /// is reported as not-claimable without attempting a save; this
    /// deliberately never reaches for the stale-claim-reclaim window
    /// <see cref="ClaimBatchForPartitionAsync"/> applies to <c>Processing</c>
    /// rows, since a row an inline caller just inserted moments ago is never
    /// legitimately stale.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is <see cref="Guid.Empty"/>.</exception>
    public virtual async Task<TMessage?> TryClaimByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "id must not be Guid.Empty.");
        }

        var message = await Messages.FirstOrDefaultAsync(m => m.Id == id, cancellationToken).ConfigureAwait(false);
        if (message is null || message.Status != MessageConstants.Status.Pending)
        {
            return null;
        }

        return await ApplyPendingClaimAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Claims the row identified by <paramref name="id"/> only when it is the
    /// <em>first</em> claimable row of its partition in dispatch order — the
    /// same <see cref="IMessage.Priority"/>-descending, then arrival order
    /// <see cref="ClaimBatchForPartitionAsync"/> uses. An inline caller can
    /// therefore never overtake earlier work of its own priority, while a
    /// higher-priority row (the instant rail) is first even when older
    /// standard rows are still queued: that queued batch work is exactly what
    /// an SCT Inst must not wait behind.
    /// </summary>
    public virtual async Task<TMessage?> TryClaimByIdIfOldestAsync(
        Guid id,
        int partitionId,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "id must not be Guid.Empty.");
        }

        var staleThreshold = TimeProvider.GetUtcNow().UtcDateTime - MessageConstants.Defaults.ProcessingTimeout;
        var oldest = await GetClaimableMessagesQuery(partitionId, staleThreshold, holdCutoff: null)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (oldest is null
            || oldest.Id != id
            || oldest.Status != MessageConstants.Status.Pending)
        {
            return null;
        }

        return await ApplyPendingClaimAsync(oldest, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TMessage?> ApplyPendingClaimAsync(
        TMessage message,
        CancellationToken cancellationToken)
    {
        message.Status = MessageConstants.Status.Processing;
        try
        {
            await DbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return message;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            foreach (var entry in ex.Entries)
            {
                entry.State = EntityState.Detached;
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DbContext.Entry(message).State = EntityState.Detached;
            throw;
        }
    }

    /// <summary>
    /// Looks up a single row by <paramref name="id"/>, or <see langword="null"/>
    /// if none exists.
    /// </summary>
    /// <inheritdoc cref="IOutboxMessageStore{TMessage}.GetStatusAsync"/>
    public virtual Task<string?> GetStatusAsync(Guid id, CancellationToken cancellationToken = default) =>
        Messages
            .AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => m.Status)
            .FirstOrDefaultAsync(cancellationToken);

    public virtual Task<TMessage?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Messages.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    /// <summary>
    /// Wraps <paramref name="operation"/> in an explicit database transaction:
    /// commits on success, rolls back (and rethrows) if it throws — so a
    /// multi-row operation that fails partway leaves no partial state. Runs
    /// through the context's execution strategy so the transaction composes
    /// correctly when Npgsql is configured with retry-on-failure.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    public virtual async Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var strategy = DbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await DbContext.Database
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await operation().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }).ConfigureAwait(false);
    }
}
