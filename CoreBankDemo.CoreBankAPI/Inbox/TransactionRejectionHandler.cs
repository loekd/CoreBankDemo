using System.Diagnostics;
using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.CoreBankAPI.Inbox;

/// <summary>Outcome of <see cref="ITransactionRejectionHandler.RejectAsync"/> (ADR-023).</summary>
public enum TransactionRejectionOutcome
{
    /// <summary>The rejection row and its <c>transaction.failed</c> event were committed in one save.</summary>
    Recorded,

    /// <summary>CoreBank already holds a row for this id; nothing was written and that row stays authoritative.</summary>
    AlreadyKnown,

    /// <summary>The request carries no usable <c>TransactionId</c>, so there is nothing to record the rejection under.</summary>
    NotRecordable,

    /// <summary>The save failed. The caller must not answer <c>400</c>: nothing stands behind it.</summary>
    StoreFailed
}

public interface ITransactionRejectionHandler
{
    Task<TransactionRejectionOutcome> RejectAsync(
        TransactionRequest? request, IReadOnlyList<string> errors, CancellationToken cancellationToken);
}

/// <summary>
/// Records a request CoreBank refused at the door (ADR-023), modelled on
/// <see cref="TransactionCancellationHandler"/>'s tombstone: a terminal inbox
/// row carrying a <c>Failed</c> response, and its <c>transaction.failed</c>
/// event, committed by <c>StoreIfNewAsync</c>'s single <c>SaveChanges</c>.
/// A <c>400</c> therefore has a published outcome behind it whenever the
/// rejection was <see cref="TransactionRejectionOutcome.Recorded"/>.
/// <see cref="TransactionRejectionOutcome.AlreadyKnown"/> means CoreBank
/// already holds a row for the id and that row's own outcome stands;
/// <see cref="TransactionRejectionOutcome.NotRecordable"/> is a plain
/// <c>400</c> for a request with no usable id (unreachable from PaymentsAPI,
/// which always sends one).
/// </summary>
internal sealed class TransactionRejectionHandler(
    IInboxMessageRepository repository,
    IOutboxEventEnqueuer enqueuer,
    CoreBankDbContext dbContext,
    IOptions<InboxProcessingOptions> inboxOptions,
    TimeProvider timeProvider,
    BusinessMetrics businessMetrics,
    ILogger<TransactionRejectionHandler> logger) : ITransactionRejectionHandler
{
    private const int MaxTransactionIdLength = 100;
    private const int MaxAccountLength = 50;
    private const decimal MaxStorableAmount = 9_999_999_999_999_999.99m; // numeric(18,2)
    private const string FallbackCurrency = "EUR";

    public async Task<TransactionRejectionOutcome> RejectAsync(
        TransactionRequest? request, IReadOnlyList<string> errors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var transactionId = request?.TransactionId;
        if (string.IsNullOrWhiteSpace(transactionId) || transactionId.Length > MaxTransactionIdLength)
        {
            logger.LogInformation("Rejected a request that carries no usable TransactionId; there is nothing to record the rejection under");
            Activity.Current?.SetTag("outcome", "not_recordable");
            return TransactionRejectionOutcome.NotRecordable;
        }

        Activity.Current?.SetTag("transaction.id", transactionId);
        var reason = string.Join("; ", errors);

        try
        {
            var existing = await repository.FindByIdempotencyKeyAsync(transactionId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                logger.LogInformation(
                    "Rejection of transaction {TransactionId} not recorded; CoreBank already holds a {Status} row for it and that row stands",
                    transactionId,
                    existing.Status);
                Activity.Current?.SetTag("outcome", "already_known");
                return TransactionRejectionOutcome.AlreadyKnown;
            }

            var now = timeProvider.GetUtcNow();
            var rejection = new InboxMessage
            {
                Id = Guid.NewGuid(),
                IdempotencyKey = transactionId,
                TransactionId = transactionId,
                FromAccount = Clamp(request!.FromAccount),
                ToAccount = Clamp(request.ToAccount),
                Amount = request.Amount is >= 0m and <= MaxStorableAmount ? request.Amount : 0m,
                Currency = request.Currency is { Length: 3 } currency ? currency : FallbackCurrency,
                PartitionId = PartitionHelper.GetPartitionId(transactionId, inboxOptions.Value.PartitionCount),
                Status = MessageConstants.Status.Completed,
                ReceivedAt = now.UtcDateTime,
                ProcessedAt = now.UtcDateTime,
                LastError = reason,
                ResponsePayload = JsonSerializer.Serialize(
                    new TransactionResponse(transactionId, MessageConstants.Status.Failed, now)),
                TraceParent = Activity.Current?.Id,
                TraceState = Activity.Current?.TraceStateString
            };

            // Enqueued before the store so StoreIfNewAsync's single SaveChanges
            // commits the rejection and its event together (AD-5).
            await enqueuer.EnqueueTransactionFailedAsync(rejection, reason, cancellationToken).ConfigureAwait(false);

            var stored = await repository.StoreIfNewAsync(rejection, cancellationToken).ConfigureAwait(false);
            if (!stored)
            {
                // Lost the unique-key race: the winner's row is authoritative.
                DetachPendingEvents(transactionId);
                logger.LogInformation(
                    "Lost the rejection store race for transaction {TransactionId}; the winner's row stands and nothing was recorded",
                    transactionId);
                Activity.Current?.SetTag("outcome", "already_known");
                return TransactionRejectionOutcome.AlreadyKnown;
            }

            RecordCommittedRejection();
            logger.LogInformation(
                "Recorded the rejection of transaction {TransactionId} with its transaction.failed event: {Reason}",
                transactionId, reason);
            Activity.Current?.SetTag("outcome", "rejected_at_intake");
            return TransactionRejectionOutcome.Recorded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DetachPendingEvents(transactionId);
            throw;
        }
        catch (Exception ex)
        {
            DetachPendingEvents(transactionId);
            logger.LogWarning(ex, "Could not record the rejection of transaction {TransactionId}", transactionId);
            Activity.Current?.SetTag("outcome", "store_failed");
            return TransactionRejectionOutcome.StoreFailed;
        }
    }

    /// <summary>
    /// Counts a committed rejection: the inbox row leaves as completed (what
    /// <c>TransactionExecutionHandler</c> records for a business rejection,
    /// which also ends <c>Completed</c>), and its <c>transaction.failed</c>
    /// event enters the messaging outbox. <see cref="IOutboxEventEnqueuer"/>
    /// adds the event row directly to the context, never through
    /// <c>StoreIfNewAsync</c>'s own store-operation recording, so this is the
    /// only place that <c>added</c> is counted (mirrors
    /// <see cref="TransactionCancellationHandler"/>). The inbox row's
    /// <c>added</c> is already counted by <c>StoreIfNewAsync</c>.
    /// </summary>
    private void RecordCommittedRejection()
    {
        businessMetrics.RecordItemProcessed(
            BusinessMetrics.StoreName.CoreBankInbox, BusinessMetrics.StoreKind.Inbox, BusinessMetrics.ItemOutcome.Completed);
        businessMetrics.RecordStoreOperation(
            BusinessMetrics.StoreName.CoreBankOutbox, BusinessMetrics.StoreKind.Outbox, BusinessMetrics.StoreOperationOutcome.Added);
    }

    private static string Clamp(string? value) =>
        value is null ? string.Empty : value.Length <= MaxAccountLength ? value : value[..MaxAccountLength];

    /// <summary>An event row for a rejection that never committed must not ride along on a later save.</summary>
    private void DetachPendingEvents(string transactionId)
    {
        var pending = dbContext.ChangeTracker.Entries<MessagingOutboxMessage>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.TransactionId == transactionId)
            .ToList();
        foreach (var entry in pending)
        {
            entry.State = EntityState.Detached;
        }
    }
}
