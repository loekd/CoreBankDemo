using System.Diagnostics;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.CoreBankAPI.Outbox;

internal sealed class MessagingOutboxProcessor : OutboxProcessorBase<MessagingOutboxMessage>
{
    public MessagingOutboxProcessor(
        IOutboxMessageStore<MessagingOutboxMessage> store,
        IDistributedLockService lockService,
        IOutboxDeliveryStrategy<MessagingOutboxMessage> deliveryStrategy,
        ActivitySource activitySource,
        TimeProvider timeProvider,
        ILogger<MessagingOutboxProcessor> logger,
        IOptions<MessagingOutboxProcessingOptions> options)
        : base(
            store,
            lockService,
            deliveryStrategy,
            activitySource,
            timeProvider,
            logger,
            new OutboxProcessorOptions
            {
                PartitionCount = options.Value.PartitionCount,
                LockExpirySeconds = options.Value.LockExpirySeconds,
                PollingInterval = TimeSpan.FromMilliseconds(options.Value.PollingIntervalMs)
            })
    {
    }

    protected override string LockNamePrefix => "messaging-outbox";
}
