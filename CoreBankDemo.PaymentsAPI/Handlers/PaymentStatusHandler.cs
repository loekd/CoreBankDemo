using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;

namespace CoreBankDemo.PaymentsAPI.Handlers;

/// <summary>
/// Reads a payment's status for <c>GET /api/payments/{transactionId}</c>
/// (spec: payment-status-get, ADR-027).
/// </summary>
public interface IPaymentStatusHandler
{
    /// <summary>
    /// Returns the payment's business status, or <see langword="null"/> when
    /// no payment has this id.
    /// </summary>
    Task<PaymentResponse?> GetAsync(string transactionId, CancellationToken cancellationToken);
}

/// <summary>
/// Projects the local outbox row onto <see cref="PaymentResponse"/>. Reports
/// the outcome CoreBank committed (<see cref="CachedTransactionOutcome"/>),
/// never the row's transport <see cref="OutboxMessage.Status"/> on its own:
/// that column turns <c>Completed</c> once CoreBank has the command in its
/// inbox, before it executes it. Never calls CoreBank (ADR-027).
/// </summary>
internal sealed class PaymentStatusHandler(IOutboxRepository repository) : IPaymentStatusHandler
{
    /// <summary>Length of the <c>IdempotencyKey</c>/<c>TransactionId</c> columns.</summary>
    internal const int MaxTransactionIdLength = 100;

    public async Task<PaymentResponse?> GetAsync(string transactionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transactionId);

        // No stored id can be longer than its column; answer without
        // depending on how the provider sizes an over-long parameter.
        if (transactionId.Length > MaxTransactionIdLength)
        {
            return null;
        }

        // PaymentStorageHandler stores TransactionId = IdempotencyKey, so the
        // indexed dedupe key finds the row (TransactionId has no index).
        var row = await repository.FindByIdempotencyKeyAsync(transactionId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var (status, processedAt) = ResolveBusinessStatus(row);
        return new PaymentResponse(
            row.IdempotencyKey,
            row.TransactionId,
            status,
            row.Amount,
            row.Currency,
            processedAt);
    }

    // The spec's status matrix, first match wins.
    private static (string Status, DateTimeOffset ProcessedAt) ResolveBusinessStatus(OutboxMessage row)
    {
        var createdAt = AsUtc(row.CreatedAt);

        if (CachedTransactionOutcome.TryRead(row.ResponsePayload) is { } cached
            && CachedTransactionOutcome.IsCommitted(cached.Status))
        {
            return (cached.Status, cached.ProcessedAt);
        }

        if (row.Status == MessageConstants.Status.Cancelled)
        {
            return (MessageConstants.Status.Cancelled, row.ProcessedAt is { } cancelledAt ? AsUtc(cancelledAt) : createdAt);
        }

        // Only rows from before ADR-023 can be Failed.
        if (row.Status == MessageConstants.Status.Failed)
        {
            return (MessageConstants.Status.Failed, createdAt);
        }

        return (MessageConstants.Status.Pending, createdAt);
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
