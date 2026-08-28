using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;

namespace CoreBankDemo.CoreBankAPI.Outbox;

internal sealed class DaprOutboxDeliveryStrategy(IEventPublisher eventPublisher)
    : IOutboxDeliveryStrategy<MessagingOutboxMessage>
{
    public Task DeliverAsync(
        MessagingOutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        object payload = message.EventType switch
        {
            Constants.TransactionCompleted => new TransactionCompletedEvent(
                message.TransactionId,
                message.TransactionStatus,
                AsUtc(message.ProcessedAt ?? message.CreatedAt)),
            Constants.TransactionFailed => new TransactionFailedEvent(
                message.TransactionId,
                message.TransactionStatus,
                AsUtc(message.ProcessedAt ?? message.CreatedAt),
                message.ErrorReason),
            Constants.BalanceUpdated => new BalanceUpdatedEvent(
                message.TransactionId,
                message.AccountNumber,
                message.Amount,
                message.NewBalance
                    ?? throw new InvalidOperationException("BalanceUpdated event requires NewBalance."),
                message.Currency),
            _ => throw new InvalidOperationException($"Unsupported event type '{message.EventType}'.")
        };

        return eventPublisher.PublishAsync(
            message.EventType,
            message.EventSource,
            message.TransactionId,
            payload,
            message.TraceParent,
            cancellationToken);
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
