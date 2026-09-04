using System.Diagnostics;
using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.CoreBankAPI.Inbox;

/// <summary>Outcome of <see cref="TransactionIntakeHandler.ProcessAsync"/> (spec-4-4; spec: add-instant-payment-rail).</summary>
public enum TransactionIntakeOutcome
{
    Accepted,

    /// <summary>
    /// A new command was stored AND its inline execution committed within
    /// the same request (<c>X-Execute-Mode: inline</c>) -- maps to
    /// <c>200 OK</c> with the final <see cref="TransactionResponse"/>, unlike
    /// <see cref="Accepted"/> which always maps to <c>202</c>.
    /// </summary>
    InlineCompleted,

    Replayed,
    InFlight,
    TransportFailed
}

/// <summary>
/// Result of <see cref="TransactionIntakeHandler.ProcessAsync"/>. Exactly one
/// of <see cref="Response"/>/<see cref="Errors"/> is populated, depending on
/// <see cref="Outcome"/> (<see cref="TransactionIntakeOutcome.TransportFailed"/>
/// carries <see cref="Errors"/> only; every other outcome carries
/// <see cref="Response"/> only).
/// </summary>
public sealed record TransactionIntakeResult(
    TransactionIntakeOutcome Outcome,
    TransactionResponse? Response,
    string[]? Errors);

/// <summary>
/// Result of <see cref="TransactionIntakeHandler.GetStatusAsync"/>.
/// <see cref="CachedResponse"/> and <see cref="StatusResponse"/> are mutually
/// exclusive — never both populated — and both are <see langword="null"/>
/// when <see cref="Found"/> is <see langword="false"/>.
/// </summary>
public sealed record TransactionStatusResult(
    bool Found,
    TransactionResponse? CachedResponse,
    TransactionStatusResponse? StatusResponse);

/// <summary>
/// Pure business logic for transaction intake (spec-4-4; AD-4, AD-11):
/// dedupe-before-any-business-logic, store a <c>Pending</c> row, and report
/// status. Never calls <see cref="TransactionValidator.Validate"/> or
/// <see cref="TransactionExecutor.ExecuteAsync"/> — those require
/// loaded/locked <see cref="Account"/> snapshots and only run during actual
/// execution (story 4.6); this handler only accepts, dedupes, and stores.
/// Returns domain types only, never <see cref="Microsoft.AspNetCore.Mvc.IActionResult"/>
/// (conventions skill: controllers stay thin, business logic lives here).
/// Public (unlike the sibling <see cref="IAccountRepository"/>/
/// <see cref="ITransactionExecutor"/> ports): <see cref="TransactionsController"/>
/// is a public ASP.NET Core controller (MVC only discovers public controller
/// types), so its constructor-injected dependency cannot be less accessible
/// than the class itself (mirrors why PaymentsAPI's <c>IOutboxRepository</c>
/// is public — same constraint, same reason).
/// </summary>
public interface ITransactionIntakeHandler
{
    /// <summary>
    /// <paramref name="executeInline"/> reproduces today's deferred-execution
    /// behaviour exactly when <see langword="false"/> (the default). When
    /// <see langword="true"/> and this call stores a brand-new command row,
    /// it also invokes the same execution handler the background inbox
    /// processor uses, inline within this request (spec:
    /// add-instant-payment-rail) -- see <see cref="TransactionIntakeOutcome.InlineCompleted"/>.
    /// </summary>
    Task<TransactionIntakeResult> ProcessAsync(
        TransactionRequest request, CancellationToken cancellationToken, bool executeInline = false);

    Task<TransactionStatusResult> GetStatusAsync(string transactionId, CancellationToken cancellationToken);
}

internal sealed class TransactionIntakeHandler(
    IInboxMessageRepository repository,
    IInboxMessageStore<InboxMessage> inboxStore,
    IInboxMessageHandler<InboxMessage> executionHandler,
    IDistributedLockService lockService,
    IOptions<InboxProcessingOptions> inboxOptions,
    TimeProvider timeProvider,
    ILogger<TransactionIntakeHandler> logger,
    BusinessMetrics businessMetrics) : ITransactionIntakeHandler
{
    public async Task<TransactionIntakeResult> ProcessAsync(
        TransactionRequest request, CancellationToken cancellationToken, bool executeInline = false)
    {
        EnrichActivityWithRequest(request);

        var now = timeProvider.GetUtcNow();

        var existing = await repository.FindByIdempotencyKeyAsync(request.TransactionId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            logger.LogInformation(
                "Duplicate transaction intake for {TransactionId}, existing status {Status}",
                request.TransactionId, existing.Status);
            return BuildIntakeResultForExisting(existing);
        }

        var partitionId = PartitionHelper.GetPartitionId(request.TransactionId, inboxOptions.Value.PartitionCount);
        var message = new InboxMessage
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = request.TransactionId,
            TransactionId = request.TransactionId,
            FromAccount = request.FromAccount,
            ToAccount = request.ToAccount,
            Amount = request.Amount,
            Currency = request.Currency,
            PartitionId = partitionId,
            Status = MessageConstants.Status.Pending,
            ReceivedAt = now.UtcDateTime,
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString
        };

        var stored = await repository.StoreIfNewAsync(message, cancellationToken).ConfigureAwait(false);
        if (stored)
        {
            logger.LogInformation(
                "Accepted transaction {TransactionId} into partition {PartitionId}",
                request.TransactionId, partitionId);
            Activity.Current?.SetTag("outcome", "accepted");
            businessMetrics.RecordTransactionIntake(BusinessMetrics.TransactionIntakeOutcome.Accepted);

            if (executeInline)
            {
                var inlineResult = await TryExecuteInlineAsync(message, cancellationToken).ConfigureAwait(false);
                if (inlineResult is not null)
                {
                    return inlineResult;
                }
            }

            var response = new TransactionResponse(request.TransactionId, MessageConstants.Status.Pending, now);
            return new TransactionIntakeResult(TransactionIntakeOutcome.Accepted, response, null);
        }

        // Lost a concurrent race for the same TransactionId (AD-4): the
        // winner's row is now visible, so re-query and branch exactly as the
        // found-on-first-check path above — never a 500 for this case.
        logger.LogInformation(
            "Lost the store race for transaction {TransactionId}; re-querying the winner's row",
            request.TransactionId);
        existing = await repository.FindByIdempotencyKeyAsync(request.TransactionId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            // Defensive only: StoreIfNewAsync reported a lost race, so a row
            // must exist. If it is somehow gone by the time we re-query, the
            // safest non-crashing outcome is to report the race loss as a
            // transport failure rather than throw.
            logger.LogWarning(
                "Transaction {TransactionId} lost the store race but no row was found on re-query",
                request.TransactionId);
            Activity.Current?.SetTag("outcome", "transport_failed");
            businessMetrics.RecordTransactionIntake(BusinessMetrics.TransactionIntakeOutcome.TransportFailed);
            return new TransactionIntakeResult(
                TransactionIntakeOutcome.TransportFailed,
                null,
                ["Failed to store or retrieve transaction"]);
        }

        return BuildIntakeResultForExisting(existing);
    }

    public async Task<TransactionStatusResult> GetStatusAsync(string transactionId, CancellationToken cancellationToken)
    {
        var existing = await repository.FindByIdempotencyKeyAsync(transactionId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            logger.LogInformation("Status requested for unknown transaction {TransactionId}", transactionId);
            return new TransactionStatusResult(false, null, null);
        }

        if (existing.Status == MessageConstants.Status.Completed &&
            TryDeserializeResponse(existing.ResponsePayload, out var cachedResponse))
        {
            return new TransactionStatusResult(true, cachedResponse, null);
        }

        // Any other status, including Failed (GET never special-cases it,
        // matching legacy) — and defensively, a Completed row whose
        // ResponsePayload is null/empty/corrupt (should not happen) falls
        // through here rather than crashing on a null deserialize.
        var statusResponse = new TransactionStatusResponse(
            existing.TransactionId, existing.Status, existing.ReceivedAt, existing.ProcessedAt);
        return new TransactionStatusResult(true, null, statusResponse);
    }

    /// <summary>
    /// Shared "found an existing row" branching for <see cref="ProcessAsync"/>
    /// (used both on the first dedupe check and on the re-query after losing a
    /// store race — spec-4-4): <c>Completed</c> with a validly-deserializing
    /// cached payload replays it verbatim; terminal <c>Failed</c> reports the
    /// cached error; anything else (<c>Pending</c>/<c>Processing</c>, and
    /// defensively a <c>Completed</c> row whose payload failed to deserialize)
    /// is reported in-flight with its current status — never a crash.
    /// </summary>
    private TransactionIntakeResult BuildIntakeResultForExisting(InboxMessage existing)
    {
        if (existing.Status == MessageConstants.Status.Completed &&
            TryDeserializeResponse(existing.ResponsePayload, out var cachedResponse))
        {
            logger.LogInformation("Replaying cached response for transaction {TransactionId}", existing.TransactionId);
            Activity.Current?.SetTag("outcome", "replayed");
            businessMetrics.RecordTransactionIntake(BusinessMetrics.TransactionIntakeOutcome.Replayed);
            return new TransactionIntakeResult(TransactionIntakeOutcome.Replayed, cachedResponse, null);
        }

        if (existing.Status == MessageConstants.Status.Failed)
        {
            logger.LogInformation("Transaction {TransactionId} previously failed", existing.TransactionId);
            Activity.Current?.SetTag("outcome", "transport_failed");
            businessMetrics.RecordTransactionIntake(BusinessMetrics.TransactionIntakeOutcome.TransportFailed);
            return new TransactionIntakeResult(
                TransactionIntakeOutcome.TransportFailed,
                null,
                [existing.LastError ?? "Transaction failed"]);
        }

        Activity.Current?.SetTag("outcome", "in_flight");
        businessMetrics.RecordTransactionIntake(BusinessMetrics.TransactionIntakeOutcome.InFlight);
        var response = new TransactionResponse(
            existing.TransactionId, existing.Status, new DateTimeOffset(existing.ReceivedAt, TimeSpan.Zero));
        return new TransactionIntakeResult(TransactionIntakeOutcome.InFlight, response, null);
    }

    /// <summary>
    /// Attempts inline execution of a just-stored, brand-new
    /// <paramref name="message"/> via the same
    /// <see cref="IInboxMessageHandler{TMessage}"/> the background inbox
    /// processor uses -- no second execution code path (spec: add-instant-
    /// payment-rail's boundaries; AD-5's atomic ledger/inbox/event commit is
    /// entirely owned by that handler, unchanged here).
    ///
    /// <para>
    /// Review loop 1: claims <paramref name="message"/>'s row via
    /// <see cref="IInboxMessageStore{TMessage}.TryClaimByIdAsync"/> (mirroring
    /// the Payments-side <c>IOutboxMessageStore&lt;OutboxMessage&gt;.TryClaimByIdAsync</c>)
    /// before ever invoking the execution handler. The background
    /// <c>InboxProcessorBase</c> protects its own claim with a partition-level
    /// distributed lock before it ever calls the same handler; without an
    /// equivalent guard here, a background poll tick landing on this row
    /// between <c>StoreIfNewAsync</c>'s commit (which makes the row visible to
    /// every other connection) and this call could execute the same ledger
    /// mutation twice. The claim's own optimistic-concurrency transition
    /// (<c>Status</c> as an EF concurrency token) makes that race impossible:
    /// exactly one caller can ever win it.
    /// </para>
    /// </summary>
    /// <returns>
    /// The <see cref="TransactionIntakeOutcome.InlineCompleted"/> result when
    /// execution committed; a <see cref="TransactionIntakeOutcome.TransportFailed"/>
    /// result when execution threw and that failure drove the row to a
    /// terminal <c>Failed</c> state (retries exhausted); <see langword="null"/>
    /// when the claim could not be won (a concurrent background batch claim
    /// already owns the row), execution threw but the row was left retryable
    /// (not yet terminal), or, defensively, when it returned without a
    /// deserializable cached response. Note that a non-null result is
    /// returned even when the partition lock's ownership was lost mid-flight
    /// (<see cref="IDistributedLockService.ExecuteWithLockAsync"/> reports
    /// <see langword="false"/> for that case too) -- as long as the callback
    /// ran and set a result, the work genuinely happened and is trusted;
    /// only a lock that was never acquired (callback never ran) falls back to
    /// <see langword="null"/>.
    /// </returns>
    private async Task<TransactionIntakeResult?> TryExecuteInlineAsync(
        InboxMessage message, CancellationToken cancellationToken)
    {
        TransactionIntakeResult? result = null;
        try
        {
            await lockService.ExecuteWithLockAsync(
                $"corebank-inbox-partition-{message.PartitionId}",
                inboxOptions.Value.LockExpirySeconds,
                async lockToken =>
                {
                    result = await ExecuteOldestInlineAsync(message, lockToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Inline execution: lock backend failed for partition {PartitionId} while executing transaction {TransactionId}; deferred to background processing",
                message.PartitionId,
                message.TransactionId);
            return null;
        }

        // ExecuteWithLockAsync returns false both when the lock was never
        // acquired (the callback above never ran, so result is still null)
        // AND when the workload ran to completion but lock ownership was
        // lost mid-flight (its own XML doc: "not reporting success for work
        // that ran without a guaranteed-exclusive lock"). In the second case
        // result is non-null -- execution genuinely committed -- and must be
        // trusted and returned rather than discarded; only a null result
        // (the callback never ran) means the lock was truly unavailable.
        return result;
    }

    private async Task<TransactionIntakeResult?> ExecuteOldestInlineAsync(
        InboxMessage message,
        CancellationToken cancellationToken)
    {
        var claimed = await inboxStore.TryClaimByIdIfOldestAsync(
            message.Id,
            message.PartitionId,
            cancellationToken).ConfigureAwait(false);
        if (claimed is null)
        {
            logger.LogInformation(
                "Inline execution for transaction {TransactionId} is not the oldest claimable row in partition {PartitionId}; leaving it for the inbox processor",
                message.TransactionId,
                message.PartitionId);
            return null;
        }

        try
        {
            await executionHandler.HandleAsync(claimed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Inline execution failed for transaction {TransactionId}; releasing the claim for the inbox processor",
                claimed.TransactionId);
            await inboxStore.MarkAsFailedWithRetryAsync(
                claimed,
                ex.Message,
                cancellationToken).ConfigureAwait(false);

            // MarkAsFailedWithRetryAsync mutates claimed.Status in place
            // (MessageRepositoryBase.ApplyFailureTransition) before returning
            // normally, so its post-call value is authoritative: Failed means
            // this call was the one that hit MaxRetryCount -- the row is now
            // terminal and the caller must be told so (matching
            // BuildIntakeResultForExisting's Failed branch) instead of
            // ProcessAsync falling through to the generic Accepted/Pending
            // response for a row that will never be retried again.
            if (claimed.Status == MessageConstants.Status.Failed)
            {
                logger.LogInformation(
                    "Transaction {TransactionId} exhausted retries during inline execution and is now terminally failed",
                    claimed.TransactionId);
                Activity.Current?.SetTag("outcome", "transport_failed");
                businessMetrics.RecordTransactionIntake(BusinessMetrics.TransactionIntakeOutcome.TransportFailed);
                return new TransactionIntakeResult(
                    TransactionIntakeOutcome.TransportFailed,
                    null,
                    [claimed.LastError ?? ex.Message]);
            }

            return null;
        }

        if (claimed.Status != MessageConstants.Status.Completed ||
            !TryDeserializeResponse(claimed.ResponsePayload, out var response) ||
            response is null)
        {
            // Defensive only (should not happen given AD-5's atomic commit):
            // never crash the request just because the just-committed
            // response could not be read back.
            logger.LogWarning(
                "Inline execution for transaction {TransactionId} did not yield a deserializable committed response; leaving the row Pending",
                claimed.TransactionId);
            return null;
        }

        Activity.Current?.SetTag("outcome", "inline_completed");
        return new TransactionIntakeResult(TransactionIntakeOutcome.InlineCompleted, response, null);
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
            // Should not happen (AD-5: the cached payload is written atomically
            // with the Completed status), but a corrupt payload must degrade to
            // the same defensive fallback as a null/empty one, never crash the
            // request (spec-4-4 matrix).
            logger.LogWarning(ex, "Failed to deserialize cached ResponsePayload; treating as missing");
            return false;
        }

        return response is not null;
    }

    private static void EnrichActivityWithRequest(TransactionRequest request)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        activity.SetTag("transaction.id", request.TransactionId);
        activity.SetTag("transaction.from_account", request.FromAccount);
        activity.SetTag("transaction.to_account", request.ToAccount);
        activity.SetTag("transaction.amount", request.Amount);
        activity.SetTag("transaction.currency", request.Currency);
    }
}
