using System.Diagnostics;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.CoreBankAPI.Outbox;

public sealed class MessagingOutboxProcessor : OutboxProcessorBase<MessagingOutboxMessage>
{
    public MessagingOutboxProcessor(
        IDistributedLockService lockService,
        IServiceScopeFactory scopeFactory,
        ActivitySource activitySource,
        TimeProvider timeProvider,
        ILogger<MessagingOutboxProcessor> logger,
        IOptions<MessagingOutboxProcessingOptions> options)
        : base(
            lockService,
            scopeFactory,
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
