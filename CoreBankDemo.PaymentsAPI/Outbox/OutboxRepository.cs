using System.Text.Json;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.PaymentsAPI.Outbox;

internal interface IOutboxRepository
{
    Task<bool> StoreIfNewAsync(OutboxMessage message, CancellationToken cancellationToken);

    Task<OutboxMessage?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records the business outcome CoreBankAPI committed for
    /// <paramref name="transactionId"/>, as learned from its
    /// <c>transaction.completed</c>/<c>transaction.failed</c> event, on the
    /// payment's <see cref="OutboxMessage.ResponsePayload"/> -- the same
    /// serialized <see cref="TransactionSubmission"/> shape the delivery
    /// path persists, so a later duplicate replay resolves it identically.
    /// Only ever upgrades a missing or non-terminal cached outcome; a payload
    /// that already carries <c>Completed</c>/<c>Failed</c> is left untouched.
    /// Never touches <see cref="OutboxMessage.Status"/> (AD-11: transport
    /// state only).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the row was updated; <see langword="false"/>
    /// when no row exists for the id or its cached outcome was already terminal.
    /// </returns>
    Task<bool> RecordCommittedOutcomeAsync(
        string transactionId,
        string status,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken);
}

internal sealed class OutboxRepository(PaymentsDbContext dbContext, TimeProvider timeProvider, BusinessMetrics businessMetrics)
    : OutboxMessageRepositoryBase<OutboxMessage, PaymentsDbContext>(dbContext, timeProvider, businessMetrics), IOutboxRepository
{
    protected override DbSet<OutboxMessage> OutboxMessages => DbContext.OutboxMessages;

    protected override BusinessMetrics.StoreName StoreName => BusinessMetrics.StoreName.PaymentsOutbox;

    public Task<OutboxMessage?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        OutboxMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(message => message.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<bool> RecordCommittedOutcomeAsync(
        string transactionId,
        string status,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken)
    {
        var message = await OutboxMessages
            .SingleOrDefaultAsync(row => row.TransactionId == transactionId, cancellationToken)
            .ConfigureAwait(false);
        if (message is null || HasCommittedOutcome(message.ResponsePayload))
        {
            return false;
        }

        // Same serializer defaults as HttpForwardOutboxDeliveryStrategy, so
        // PaymentsController.ResolveDeliveredResponse reads both alike.
        message.ResponsePayload = JsonSerializer.Serialize(
            new TransactionSubmission(transactionId, status, processedAt));

        // A DbUpdateConcurrencyException (Status is the row's concurrency
        // token, and the outbox processor may be transitioning it right now)
        // deliberately propagates: the inbox kernel then retries the event on
        // its next tick, which is exactly the right outcome for a lost race.
        await DbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool HasCommittedOutcome(string? responsePayload)
    {
        if (string.IsNullOrEmpty(responsePayload))
        {
            return false;
        }

        try
        {
            var cached = JsonSerializer.Deserialize<TransactionSubmission>(responsePayload);
            return cached?.Status is MessageConstants.Status.Completed or MessageConstants.Status.Failed;
        }
        catch (JsonException)
        {
            // A corrupt payload is never a committed outcome; overwrite it.
            return false;
        }
    }
}
