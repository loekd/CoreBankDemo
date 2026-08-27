using System.Diagnostics;
using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.CoreBankAPI.Inbox;

/// <summary>Outcome of <see cref="TransactionIntakeHandler.ProcessAsync"/> (spec-4-4).</summary>
public enum TransactionIntakeOutcome
{
    Accepted,
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
    Task<TransactionIntakeResult> ProcessAsync(TransactionRequest request, CancellationToken cancellationToken);

    Task<TransactionStatusResult> GetStatusAsync(string transactionId, CancellationToken cancellationToken);
}

internal sealed class TransactionIntakeHandler(
    IInboxMessageRepository repository,
    IOptions<InboxProcessingOptions> inboxOptions,
    TimeProvider timeProvider,
    ILogger<TransactionIntakeHandler> logger) : ITransactionIntakeHandler
{
    public async Task<TransactionIntakeResult> ProcessAsync(TransactionRequest request, CancellationToken cancellationToken)
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
            return new TransactionIntakeResult(TransactionIntakeOutcome.Replayed, cachedResponse, null);
        }

        if (existing.Status == MessageConstants.Status.Failed)
        {
            logger.LogInformation("Transaction {TransactionId} previously failed", existing.TransactionId);
            Activity.Current?.SetTag("outcome", "transport_failed");
            return new TransactionIntakeResult(
                TransactionIntakeOutcome.TransportFailed,
                null,
                [existing.LastError ?? "Transaction failed"]);
        }

        Activity.Current?.SetTag("outcome", "in_flight");
        var response = new TransactionResponse(
            existing.TransactionId, existing.Status, new DateTimeOffset(existing.ReceivedAt, TimeSpan.Zero));
        return new TransactionIntakeResult(TransactionIntakeOutcome.InFlight, response, null);
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
