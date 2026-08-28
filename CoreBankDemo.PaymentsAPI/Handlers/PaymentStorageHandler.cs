using System.Diagnostics;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.PaymentsAPI.Handlers;

public sealed record PaymentStorageResult(OutboxMessage Payment, bool IsNew);

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
    TimeProvider timeProvider) : IPaymentStorageHandler
{
    public async Task<PaymentStorageResult> StoreAsync(
        PaymentRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = idempotencyKey ?? Guid.NewGuid().ToString("D");
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = key,
            TransactionId = key,
            FromAccount = request.FromAccount,
            ToAccount = request.ToAccount,
            Amount = request.Amount,
            Currency = request.Currency,
            PartitionId = PartitionHelper.GetPartitionId(key, options.Value.PartitionCount),
            Status = MessageConstants.Status.Pending,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString
        };

        if (await repository.StoreIfNewAsync(message, cancellationToken).ConfigureAwait(false))
        {
            return new PaymentStorageResult(message, true);
        }

        var winner = await repository.FindByIdempotencyKeyAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Payment store rejected idempotency key '{key}' as a duplicate, but the persisted winner was not found.");

        return new PaymentStorageResult(winner, false);
    }
}
