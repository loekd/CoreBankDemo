using System.Diagnostics;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.PaymentsAPI.Handlers;

public enum PaymentStorageOutcome
{
    Stored,
    Duplicate,
    ValidationFailed
}

public sealed record PaymentSnapshot(
    Guid Id,
    string IdempotencyKey,
    string TransactionId,
    string FromAccount,
    string ToAccount,
    decimal Amount,
    string Currency,
    int PartitionId,
    string Status,
    DateTime CreatedAt,
    string? TraceParent,
    string? TraceState,
    string? ResponsePayload = null);

public sealed record PaymentStorageResult(
    PaymentStorageOutcome Outcome,
    PaymentSnapshot? Payment,
    IReadOnlyList<string> Errors);

public interface IPaymentStorageHandler
{
    Task<PaymentStorageResult> StoreAsync(
        PaymentRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken);
}

internal sealed class PaymentStorageHandler(
    IOutboxRepository repository,
    IOptions<OutboxProcessingOptions> options,
    TimeProvider timeProvider,
    ILogger<PaymentStorageHandler> logger,
    BusinessMetrics businessMetrics) : IPaymentStorageHandler
{
    public async Task<PaymentStorageResult> StoreAsync(
        PaymentRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scheme = ToMetricScheme(request.Scheme);

        if (idempotencyKey is not null && (idempotencyKey.Length is < 1 or > 100))
        {
            businessMetrics.RecordPaymentIntake(BusinessMetrics.PaymentOutcome.ValidationFailed, scheme);
            return new PaymentStorageResult(
                PaymentStorageOutcome.ValidationFailed,
                null,
                ["Idempotency key must be between 1 and 100 characters."]);
        }

        var key = idempotencyKey ?? Guid.NewGuid().ToString("D");
        var partitionId = PartitionHelper.GetPartitionId(key, options.Value.PartitionCount);
        var normalizedAmount = decimal.Round(request.Amount, 2, MidpointRounding.ToEven);
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = key,
            TransactionId = key,
            FromAccount = request.FromAccount,
            ToAccount = request.ToAccount,
            Amount = normalizedAmount,
            Currency = request.Currency,
            PartitionId = partitionId,
            Status = MessageConstants.Status.Pending,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString
        };

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["IdempotencyKey"] = key,
            ["PartitionId"] = partitionId
        });

        if (await repository.StoreIfNewAsync(message, cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation(
                "Stored payment {IdempotencyKey} in partition {PartitionId}",
                key,
                partitionId);
            businessMetrics.RecordPaymentIntake(BusinessMetrics.PaymentOutcome.Stored, scheme);
            return new PaymentStorageResult(PaymentStorageOutcome.Stored, ToSnapshot(message), []);
        }

        logger.LogInformation(
            "Payment {IdempotencyKey} already exists in partition {PartitionId}; loading persisted winner",
            key,
            partitionId);
        var winner = await repository.FindByIdempotencyKeyAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Payment store reported duplicate idempotency key '{key}', but no persisted winner was found.");

        businessMetrics.RecordPaymentIntake(BusinessMetrics.PaymentOutcome.Duplicate, scheme);
        return new PaymentStorageResult(PaymentStorageOutcome.Duplicate, ToSnapshot(winner), []);
    }

    /// <summary>
    /// Maps the request's already-validated closed <c>Scheme</c> string (see
    /// <see cref="PaymentSchemes"/>) onto the metric contract's closed
    /// <see cref="BusinessMetrics.PaymentScheme"/> vocabulary -- never copies
    /// the raw string into a metric attribute.
    /// </summary>
    private static BusinessMetrics.PaymentScheme ToMetricScheme(string scheme) =>
        string.Equals(scheme, PaymentSchemes.Instant, StringComparison.Ordinal)
            ? BusinessMetrics.PaymentScheme.Instant
            : BusinessMetrics.PaymentScheme.Standard;

    private static PaymentSnapshot ToSnapshot(OutboxMessage message) => new(
        message.Id,
        message.IdempotencyKey,
        message.TransactionId,
        message.FromAccount,
        message.ToAccount,
        message.Amount,
        message.Currency,
        message.PartitionId,
        message.Status,
        message.CreatedAt,
        message.TraceParent,
        message.TraceState,
        message.ResponsePayload);
}
