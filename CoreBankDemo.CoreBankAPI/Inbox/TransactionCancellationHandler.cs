using System.Diagnostics;
using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.CoreBankAPI.Inbox;

/// <summary>Outcome of <see cref="ITransactionCancellationHandler.CancelAsync"/> (spec: instant-rail-timeout-cancel).</summary>
public enum TransactionCancellationOutcome
{
    /// <summary>
    /// The command is provably dead: a <c>Cancelled</c> tombstone was stored
    /// before the original ever arrived, a still-<c>Pending</c> row was
    /// cancelled, or an earlier cancel is replayed. Maps to <c>200</c> with a
    /// <c>Cancelled</c> <see cref="TransactionResponse"/>.
    /// </summary>
    Cancelled,

    /// <summary>
    /// CoreBank already executed the command (business success or business
    /// rejection). Maps to <c>200</c> with the cached committed
    /// <see cref="TransactionResponse"/> -- the caller must report that, never
    /// a cancellation.
    /// </summary>
    AlreadyCommitted,

    /// <summary>
    /// The row cannot be cancelled: it is <c>Processing</c> under a live claim,
    /// terminally <c>Failed</c>, or lost the claim race. Maps to <c>409</c>
    /// carrying the current status; the caller keeps its honest unknown.
    /// </summary>
    InFlight,

    /// <summary>Defensive: the tombstone lost its store race and no row was found on re-query.</summary>
    StoreFailed
}

/// <summary>
/// Result of <see cref="ITransactionCancellationHandler.CancelAsync"/>. Exactly
/// one of <see cref="Response"/>/<see cref="Errors"/> is populated
/// (<see cref="TransactionCancellationOutcome.StoreFailed"/> carries
/// <see cref="Errors"/> only).
/// </summary>
public sealed record TransactionCancellationResult(
    TransactionCancellationOutcome Outcome,
    TransactionResponse? Response,
    string[]? Errors);

/// <summary>
/// Cancellation path for the instant rail (spec: instant-rail-timeout-cancel):
/// tombstones a command CoreBank has not received yet, cancels a command it
/// stored but has not executed, or reports the committed outcome when it
/// already executed -- so PaymentsAPI can answer <c>504</c>/<c>Cancelled</c>
/// only once nothing can execute any more. Never touches the ledger. Every
/// cancellation it commits -- tombstone or pending-row cancel -- is broadcast
/// as a <c>transaction.cancelled</c> event enqueued in the same save as the
/// cancel (spec: instant-rail-cancelled-event, AD-5): the reply to
/// PaymentsAPI can be lost, the outbox row cannot, so a residual
/// <c>202</c> still learns its outcome. A replayed cancellation publishes
/// nothing (it was published once already), and a cancel that never
/// reached CoreBank is PaymentsAPI's alone. Public for the same reason as
/// <see cref="ITransactionIntakeHandler"/>: a public controller's
/// constructor dependency cannot be less accessible than the controller.
/// </summary>
public interface ITransactionCancellationHandler
{
    /// <summary>
    /// <paramref name="request"/> is the original <see cref="TransactionRequest"/>,
    /// so a tombstone carries the same columns as a real command and
    /// <c>StoreIfNewAsync</c>'s unique key makes "cancel before original" and
    /// "original before cancel" symmetric (AD-4). <paramref name="priority"/>
    /// is stored on the tombstone like on a real command.
    /// </summary>
    Task<TransactionCancellationResult> CancelAsync(
        TransactionRequest request,
        CancellationToken cancellationToken,
        int priority = MessageConstants.Priority.Standard);
}

internal sealed class TransactionCancellationHandler(
    IInboxMessageRepository repository,
    IInboxMessageStore<InboxMessage> inboxStore,
    IOutboxEventEnqueuer enqueuer,
    CoreBankDbContext dbContext,
    IOptions<InboxProcessingOptions> inboxOptions,
    TimeProvider timeProvider,
    ILogger<TransactionCancellationHandler> logger) : ITransactionCancellationHandler
{
    /// <summary>Recorded as <c>LastError</c> on every row this handler cancels.</summary>
    internal const string CancellationReason = "Cancelled by the instant rail on budget exhaustion";

    public async Task<TransactionCancellationResult> CancelAsync(
        TransactionRequest request,
        CancellationToken cancellationToken,
        int priority = MessageConstants.Priority.Standard)
    {
        ArgumentNullException.ThrowIfNull(request);
        Activity.Current?.SetTag("transaction.id", request.TransactionId);

        var existing = await repository.FindByIdempotencyKeyAsync(request.TransactionId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return await ResolveExistingAsync(existing, allowClaim: true, cancellationToken).ConfigureAwait(false);
        }

        // Never received: store a terminal tombstone with the command's own
        // columns, so a late-arriving original dedupes against it and replays
        // Cancelled instead of executing (AD-4: insert-then-catch, never
        // check-then-insert -- losing the race below is a normal outcome).
        var now = timeProvider.GetUtcNow();
        var response = new TransactionResponse(request.TransactionId, MessageConstants.Status.Cancelled, now);
        var tombstone = new InboxMessage
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = request.TransactionId,
            TransactionId = request.TransactionId,
            FromAccount = request.FromAccount,
            ToAccount = request.ToAccount,
            Amount = request.Amount,
            Currency = request.Currency,
            PartitionId = PartitionHelper.GetPartitionId(request.TransactionId, inboxOptions.Value.PartitionCount),
            Status = MessageConstants.Status.Cancelled,
            Priority = priority,
            ReceivedAt = now.UtcDateTime,
            ProcessedAt = now.UtcDateTime,
            LastError = CancellationReason,
            ResponsePayload = JsonSerializer.Serialize(response),
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString
        };

        // Enqueued before the store so StoreIfNewAsync's single SaveChanges
        // commits the tombstone and its event together: never an event
        // without a committed cancel, never a committed cancel without its
        // event. The tombstone's ProcessedAt is already stamped, so the
        // event's EventOccurredAt is the cancellation time the cached
        // payload carries.
        var cancelledEvent = await enqueuer.EnqueueTransactionCancelledAsync(tombstone, CancellationReason, cancellationToken)
            .ConfigureAwait(false);
        bool stored;
        try
        {
            stored = await repository.StoreIfNewAsync(tombstone, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // StoreIfNewAsync detached the tombstone; the event row is ours
            // to detach, or the next save on this context would insert an
            // event for a cancel that never committed.
            Detach(cancelledEvent);
            throw;
        }

        if (stored)
        {
            logger.LogInformation(
                "Stored a Cancelled tombstone for transaction {TransactionId} in partition {PartitionId} with its transaction.cancelled event; the original command will replay it",
                request.TransactionId,
                tombstone.PartitionId);
            Activity.Current?.SetTag("outcome", "cancelled_tombstone");
            return new TransactionCancellationResult(TransactionCancellationOutcome.Cancelled, response, null);
        }

        // Unique-key loss: the whole save rolled back, so the event row must
        // go too (mirrors StoreIfNewAsync's own detach-on-failure discipline).
        Detach(cancelledEvent);

        // The original arrived between the lookup and the insert: resolve
        // against the winner's row exactly as if it had been found first.
        logger.LogInformation(
            "Lost the tombstone store race for transaction {TransactionId}; re-querying the winner's row",
            request.TransactionId);
        existing = await repository.FindByIdempotencyKeyAsync(request.TransactionId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            logger.LogWarning(
                "Transaction {TransactionId} lost the tombstone store race but no row was found on re-query",
                request.TransactionId);
            Activity.Current?.SetTag("outcome", "store_failed");
            return new TransactionCancellationResult(
                TransactionCancellationOutcome.StoreFailed,
                null,
                ["Failed to store or retrieve transaction"]);
        }

        return await ResolveExistingAsync(existing, allowClaim: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Branches on the row CoreBank already holds. Only a <c>Pending</c> row is
    /// ever cancelled, and only after this call claims it itself
    /// (<see cref="IInboxMessageStore{TMessage}.TryClaimByIdAsync"/>, the same
    /// <c>Status</c>-as-concurrency-token transition the inbox processor and
    /// the inline path use), so a cancel and an execution can never both win.
    /// <paramref name="allowClaim"/> is <see langword="false"/> on the single
    /// re-resolution after a lost claim, so a row that is somehow still
    /// <c>Pending</c> then is reported in flight rather than looped on.
    /// </summary>
    private async Task<TransactionCancellationResult> ResolveExistingAsync(
        InboxMessage existing, bool allowClaim, CancellationToken cancellationToken)
    {
        switch (existing.Status)
        {
            case MessageConstants.Status.Completed:
                if (TryDeserializeResponse(existing.ResponsePayload, out var committed))
                {
                    logger.LogInformation(
                        "Cancel for transaction {TransactionId} arrived after it committed; reporting the committed outcome",
                        existing.TransactionId);
                    Activity.Current?.SetTag("outcome", "already_committed");
                    return new TransactionCancellationResult(TransactionCancellationOutcome.AlreadyCommitted, committed, null);
                }

                // Defensive (AD-5 writes the payload atomically with Completed):
                // a Completed row whose outcome cannot be read back is reported
                // in flight -- never cancelled, never invented.
                return InFlight(existing);

            case MessageConstants.Status.Cancelled:
                Activity.Current?.SetTag("outcome", "cancelled_replayed");
                return new TransactionCancellationResult(
                    TransactionCancellationOutcome.Cancelled,
                    TryDeserializeResponse(existing.ResponsePayload, out var cached)
                        ? cached
                        : new TransactionResponse(
                            existing.TransactionId,
                            MessageConstants.Status.Cancelled,
                            new DateTimeOffset(existing.ProcessedAt ?? existing.ReceivedAt, TimeSpan.Zero)),
                    null);

            case MessageConstants.Status.Pending when allowClaim:
                return await CancelPendingAsync(existing, cancellationToken).ConfigureAwait(false);

            default:
                // Processing under a live claim, terminally Failed, or still
                // Pending after a lost claim: the boundary says never cancel
                // these -- the caller keeps its honest unknown.
                return InFlight(existing);
        }
    }

    private async Task<TransactionCancellationResult> CancelPendingAsync(
        InboxMessage existing, CancellationToken cancellationToken)
    {
        var claimed = await inboxStore.TryClaimByIdAsync(existing.Id, cancellationToken).ConfigureAwait(false);
        if (claimed is null)
        {
            // The inbox processor or the inline path won the row first. Its
            // outcome is theirs to report: re-read once and branch without
            // claiming again.
            logger.LogInformation(
                "Cancel for transaction {TransactionId} lost the claim race; reporting the row's current state",
                existing.TransactionId);
            var current = await repository.FindByIdempotencyKeyAsync(existing.TransactionId, cancellationToken)
                .ConfigureAwait(false);
            return current is null
                ? new TransactionCancellationResult(
                    TransactionCancellationOutcome.StoreFailed, null, ["Failed to store or retrieve transaction"])
                : await ResolveExistingAsync(current, allowClaim: false, cancellationToken).ConfigureAwait(false);
        }

        var now = timeProvider.GetUtcNow();
        var response = new TransactionResponse(claimed.TransactionId, MessageConstants.Status.Cancelled, now);
        // Mutated on the same tracked entity MarkAsCancelledAsync saves, so the
        // cached payload commits together with the terminal status (mirrors
        // how ForwardAsync/MarkAsCompletedAsync share one SaveChanges).
        claimed.ResponsePayload = JsonSerializer.Serialize(response);
        // A Pending row carries no ProcessedAt yet, and the event's
        // EventOccurredAt is taken from it: stamp the same instant the cached
        // payload carries, so the event and the 200/504 answer agree.
        // MarkAsCancelledAsync re-stamps the row from the same TimeProvider,
        // and a reload on conflict discards this along with the payload.
        claimed.ProcessedAt = now.UtcDateTime;
        // Enqueued before the transition so MarkAsCancelledAsync's single
        // SaveChanges commits the cancel and its event together; kept only
        // when that save applied the cancel.
        var cancelledEvent = await enqueuer.EnqueueTransactionCancelledAsync(claimed, CancellationReason, cancellationToken)
            .ConfigureAwait(false);
        MessageTransitionOutcome transition;
        try
        {
            transition = await inboxStore.MarkAsCancelledAsync(claimed, CancellationReason, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            Detach(cancelledEvent);
            throw;
        }

        if (transition != MessageTransitionOutcome.Applied)
        {
            Detach(cancelledEvent);
        }

        switch (transition)
        {
            case MessageTransitionOutcome.Applied:
                logger.LogInformation(
                    "Cancelled pending transaction {TransactionId} in partition {PartitionId} before execution and enqueued its transaction.cancelled event",
                    claimed.TransactionId,
                    claimed.PartitionId);
                Activity.Current?.SetTag("outcome", "cancelled");
                return new TransactionCancellationResult(TransactionCancellationOutcome.Cancelled, response, null);

            case MessageTransitionOutcome.AlreadyTerminal:
                // The reload inside MarkAsCancelledAsync refreshed the tracked
                // row: report whatever terminal state it now holds.
                return await ResolveExistingAsync(claimed, allowClaim: false, cancellationToken).ConfigureAwait(false);

            default:
                return InFlight(claimed);
        }
    }

    /// <summary>
    /// Drops an event row whose cancel did not commit from the change tracker,
    /// so the context stays usable for the caller's next save (the same
    /// discipline <c>StoreIfNewAsync</c> applies to its own row on failure).
    /// </summary>
    private void Detach(MessagingOutboxMessage cancelledEvent) =>
        dbContext.Entry(cancelledEvent).State = EntityState.Detached;

    private TransactionCancellationResult InFlight(InboxMessage existing)
    {
        logger.LogInformation(
            "Cancel for transaction {TransactionId} refused; the row is {Status}",
            existing.TransactionId,
            existing.Status);
        Activity.Current?.SetTag("outcome", "in_flight");
        return new TransactionCancellationResult(
            TransactionCancellationOutcome.InFlight,
            new TransactionResponse(
                existing.TransactionId,
                existing.Status,
                new DateTimeOffset(existing.ProcessedAt ?? existing.ReceivedAt, TimeSpan.Zero)),
            null);
    }

    private bool TryDeserializeResponse(string? responsePayload, out TransactionResponse? response)
    {
        response = null;
        if (string.IsNullOrEmpty(responsePayload))
        {
            return false;
        }

        try
        {
            response = JsonSerializer.Deserialize<TransactionResponse>(responsePayload);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize cached ResponsePayload; treating as missing");
            return false;
        }

        return response is not null;
    }
}
