using System.Diagnostics;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
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
    ValidationFailed,
    /// <summary>Refused at the door by the local account projection (ADR-028); nothing stored.</summary>
    InsufficientFunds
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
    IOptions<InstantRailOptions> instantRail,
    TimeProvider timeProvider,
    ILogger<PaymentStorageHandler> logger,
    BusinessMetrics businessMetrics) : IPaymentStorageHandler
{
    /// <summary>The only text a refused caller sees; amounts never leave the log and the span.</summary>
    internal const string InsufficientFundsError = "Insufficient funds";

    /// <summary>Span tag value for the traces dashboard's failed-payments table.</summary>
    internal const string InsufficientFundsReason = "insufficient_funds";

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
        // ADR-026: the debtor account picks the partition, not the key, so two
        // debits from one account sit in the same lane and are forwarded in
        // the order they were accepted. The key stays the dedupe identity.
        var partitionId = PartitionHelper.GetPartitionId(request.FromAccount, options.Value.PartitionCount);
        var normalizedAmount = decimal.Round(request.Amount, 2, MidpointRounding.ToEven);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var isInstant = scheme == BusinessMetrics.PaymentScheme.Instant;
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
            // An SCT Inst is claimed ahead of any queued batch (SCT) work in
            // its partition -- for the inline attempt and, if that is
            // deferred, for the background processor alike.
            Priority = isInstant ? MessageConstants.Priority.Instant : MessageConstants.Priority.Standard,
            // Left to the inline request for the length of its budget; the
            // batch rail takes over only once that has lapsed.
            HoldUntil = isInstant ? now.AddMilliseconds(instantRail.Value.BudgetMilliseconds) : null,
            CreatedAt = now,
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString
        };

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["IdempotencyKey"] = key,
            ["PartitionId"] = partitionId
        });

        var acceptance = await repository.AcceptAsync(message, cancellationToken).ConfigureAwait(false);
        switch (acceptance)
        {
            case PaymentAcceptance.Stored:
                logger.LogInformation(
                    "Stored payment {IdempotencyKey} in partition {PartitionId}",
                    key,
                    partitionId);
                businessMetrics.RecordPaymentIntake(BusinessMetrics.PaymentOutcome.Stored, scheme);
                return new PaymentStorageResult(PaymentStorageOutcome.Stored, ToSnapshot(message), []);

            case PaymentAcceptance.InsufficientFunds:
                // ADR-028: a door refusal. Nothing was accepted, so CoreBank
                // stays the only source of outcomes; the figures stay in the
                // log and on the span, never in the response.
                logger.LogWarning(
                    "Refused payment {IdempotencyKey} in partition {PartitionId}: account {FromAccount} is known to be short of {Amount} {Currency}",
                    key,
                    partitionId,
                    request.FromAccount,
                    normalizedAmount,
                    request.Currency);
                var activity = Activity.Current;
                activity?.SetTag(FailedPaymentTags.Outcome, FailedPaymentTags.Rejected);
                activity?.SetTag(FailedPaymentTags.FailureReason, InsufficientFundsReason);
                activity?.SetTag(FailedPaymentTags.TransactionId, key);
                businessMetrics.RecordPaymentIntake(BusinessMetrics.PaymentOutcome.InsufficientFunds, scheme);
                return new PaymentStorageResult(PaymentStorageOutcome.InsufficientFunds, null, [InsufficientFundsError]);

            case PaymentAcceptance.Duplicate:
                break;

            default:
                throw new InvalidOperationException($"Unhandled payment acceptance: {acceptance}");
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
