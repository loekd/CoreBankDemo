using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;

namespace CoreBankDemo.CoreBankAPI.Outbox;

internal sealed class DaprOutboxDeliveryStrategy(IEventPublisher publisher)
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
                AsUtcOffset(message.EventOccurredAt)),
            Constants.TransactionFailed => new TransactionFailedEvent(
                message.TransactionId,
                message.TransactionStatus,
                AsUtcOffset(message.EventOccurredAt),
                message.ErrorReason),
            Constants.BalanceUpdated => new BalanceUpdatedEvent(
                message.TransactionId,
                message.AccountNumber,
                message.Amount,
                message.NewBalance
                ?? throw new InvalidOperationException(
                    $"BalanceUpdated outbox message {message.Id} is missing NewBalance."),
                message.Currency),
            _ => throw new NotSupportedException(
                $"Unsupported messaging outbox event type '{message.EventType}'.")
        };

        return publisher.PublishAsync(
            message.EventType,
            message.EventSource,
            message.TransactionId,
            payload,
            message.TraceParent,
            cancellationToken);
    }

    private static DateTimeOffset AsUtcOffset(DateTime value) =>
        value.Kind == DateTimeKind.Local
            ? new DateTimeOffset(value.ToUniversalTime())
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
