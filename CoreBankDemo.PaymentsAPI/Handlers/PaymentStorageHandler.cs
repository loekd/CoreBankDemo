using System.Diagnostics;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
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
    string? TraceState);

public sealed record PaymentStorageResult(
    PaymentStorageOutcome Outcome,
    PaymentSnapshot? Payment,
    IReadOnlyList<string> Errors);

internal interface IPaymentStorageHandler
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
    ILogger<PaymentStorageHandler> logger) : IPaymentStorageHandler
{
    public async Task<PaymentStorageResult> StoreAsync(
        PaymentRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (idempotencyKey is not null && (idempotencyKey.Length is < 1 or > 100))
        {
            return new PaymentStorageResult(
                PaymentStorageOutcome.ValidationFailed,
                null,
                ["Idempotency key must be between 1 and 100 characters."]);
        }

        var key = idempotencyKey ?? Guid.NewGuid().ToString("D");
        var partitionId = PartitionHelper.GetPartitionId(key, options.Value.PartitionCount);
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = key,
            TransactionId = key,
            FromAccount = request.FromAccount,
            ToAccount = request.ToAccount,
            Amount = request.Amount,
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
            return new PaymentStorageResult(PaymentStorageOutcome.Stored, ToSnapshot(message), []);
        }

        logger.LogInformation(
            "Payment {IdempotencyKey} already exists in partition {PartitionId}; loading persisted winner",
            key,
            partitionId);
        var winner = await repository.FindByIdempotencyKeyAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Payment store reported duplicate idempotency key '{key}', but no persisted winner was found.");

        return new PaymentStorageResult(PaymentStorageOutcome.Duplicate, ToSnapshot(winner), []);
    }

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
        message.TraceState);
}
