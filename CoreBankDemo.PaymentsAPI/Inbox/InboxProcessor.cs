using System.Diagnostics;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.PaymentsAPI.Inbox;

/// <summary>
/// Story 5.6's concrete kernel processor for PaymentsAPI's event inbox --
/// mirrors <see cref="CoreBankDemo.CoreBankAPI.Inbox.InboxProcessor"/>
/// exactly (messaging-patterns skill's sibling reference), reusing
/// <see cref="InboxProcessorBase{TMessage}"/> unchanged and specializing only
/// <see cref="LockNamePrefix"/> and the validated
/// <see cref="InboxProcessingOptions"/>-to-<see cref="InboxProcessorOptions"/>
/// mapping. Never reimplements polling, partition fan-out, locking,
/// claiming, retry, poison classification, completion, or trace restoration
/// (boundaries).
/// </summary>
public class InboxProcessor : InboxProcessorBase<InboxMessage>
{
    public InboxProcessor(
        IDistributedLockService lockService,
        IServiceScopeFactory scopeFactory,
        ActivitySource activitySource,
        TimeProvider timeProvider,
        ILogger<InboxProcessor> logger,
        IOptions<InboxProcessingOptions> options)
        : base(
            lockService,
            scopeFactory,
            activitySource,
            timeProvider,
            logger,
            new InboxProcessorOptions
            {
                PartitionCount = options.Value.PartitionCount,
                LockExpirySeconds = options.Value.LockExpirySeconds,
                PollingInterval = TimeSpan.FromMilliseconds(options.Value.PollingIntervalMs)
            })
    {
    }

    protected override string LockNamePrefix => "payments-inbox";
}
