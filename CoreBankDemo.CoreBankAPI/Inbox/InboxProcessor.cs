using System.Diagnostics;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.CoreBankAPI.Inbox;

public class InboxProcessor : InboxProcessorBase<InboxMessage>
{
    public InboxProcessor(
        IDistributedLockService lockService,
        IServiceScopeFactory scopeFactory,
        ActivitySource activitySource,
        TimeProvider timeProvider,
        ILogger<InboxProcessor> logger,
        BusinessMetrics businessMetrics,
        IOptions<InboxProcessingOptions> options,
        IProcessorStartGate? startGate = null)
        : base(
            lockService,
            scopeFactory,
            activitySource,
            timeProvider,
            logger,
            businessMetrics,
            new InboxProcessorOptions
            {
                PartitionCount = options.Value.PartitionCount,
                LockExpirySeconds = options.Value.LockExpirySeconds,
                PollingInterval = TimeSpan.FromMilliseconds(options.Value.PollingIntervalMs)
            },
            startGate)
    {
    }

    protected override string LockNamePrefix => "corebank-inbox";

    protected override BusinessMetrics.StoreName StoreName => BusinessMetrics.StoreName.CoreBankInbox;
}
