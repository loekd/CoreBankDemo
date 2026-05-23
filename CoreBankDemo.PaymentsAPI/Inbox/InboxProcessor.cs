using System.Text.Json;
using CoreBankDemo.Messaging.Inbox;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.PaymentsAPI.Inbox;

public class InboxProcessor : InboxProcessorBase<InboxMessage, PaymentsDbContext>
{
    public InboxProcessor(
        IServiceProvider serviceProvider,
        ILogger<InboxProcessor> logger,
        IDistributedLockService lockService,
        IOptions<InboxProcessingOptions> options,
        TimeProvider timeProvider)
        : base(serviceProvider, logger, lockService, options, timeProvider, nameof(InboxProcessor))
    {
    }

    protected override string LockNamePrefix => "payments-inbox";

    protected override async Task ProcessMessageAsync(
        InboxMessage message,
        IServiceProvider scopedServiceProvider,
        CancellationToken cancellationToken)
    {
        var repository = GetService<IInboxMessageRepository>(scopedServiceProvider);
        var handler = GetService<ITransactionEventHandler>(scopedServiceProvider);

        await repository.MarkAsProcessingAsync(message, cancellationToken);

        await repository.ExecuteInTransactionAsync(
            message,
            ct => DispatchEventAsync(handler, message, ct),
            cancellationToken);

        var logger = scopedServiceProvider.GetRequiredService<ILogger<InboxProcessor>>();
        logger.LogInformation(
            "Successfully processed inbox message {MessageId} with idempotency key {IdempotencyKey}",
            message.Id, message.IdempotencyKey);
    }

    private static async Task DispatchEventAsync(
        ITransactionEventHandler handler,
        InboxMessage message,
        CancellationToken cancellationToken)
    {
        switch (message.EventType)
        {
            case nameof(TransactionCompletedEvent):
                await handler.HandleAsync(Deserialize<TransactionCompletedEvent>(message), cancellationToken);
                break;

            case nameof(TransactionFailedEvent):
                await handler.HandleAsync(Deserialize<TransactionFailedEvent>(message), cancellationToken);
                break;

            case nameof(BalanceUpdatedEvent):
                await handler.HandleAsync(Deserialize<BalanceUpdatedEvent>(message), cancellationToken);
                break;

            default:
                throw new InvalidOperationException($"Unknown event type: {message.EventType}");
        }
    }

    private static T Deserialize<T>(InboxMessage message) =>
        JsonSerializer.Deserialize<T>(message.EventPayload)
        ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name} from inbox message {message.Id}");
}
