using System.Diagnostics;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.PaymentsAPI.Outbox;

/// <summary>
/// Concrete <see cref="OutboxProcessorBase{TMessage}"/> for PaymentsAPI's
/// payment outbox (story 5.4), mirroring
/// <c>CoreBankDemo.CoreBankAPI.Outbox.MessagingOutboxProcessor</c>'s shape
/// exactly: only <see cref="LockNamePrefix"/> and the
/// <see cref="OutboxProcessingOptions"/>-to-<see cref="OutboxProcessorOptions"/>
/// mapping are overridden here — polling, partition fan-out, locking,
/// claiming, and retry/terminal-failure classification all stay owned by the
/// base class. Delivery itself is <see cref="HttpForwardOutboxDeliveryStrategy"/>.
/// </summary>
public sealed class PaymentsOutboxProcessor : OutboxProcessorBase<OutboxMessage>
{
    public PaymentsOutboxProcessor(
        IDistributedLockService lockService,
        IServiceScopeFactory scopeFactory,
        ActivitySource activitySource,
        TimeProvider timeProvider,
        ILogger<PaymentsOutboxProcessor> logger,
        BusinessMetrics businessMetrics,
        IOptions<OutboxProcessingOptions> options)
        : base(
            lockService,
            scopeFactory,
            activitySource,
            timeProvider,
            logger,
            businessMetrics,
            new OutboxProcessorOptions
            {
                PartitionCount = options.Value.PartitionCount,
                LockExpirySeconds = options.Value.LockExpirySeconds,
                PollingInterval = TimeSpan.FromMilliseconds(options.Value.PollingIntervalMs)
            })
    {
    }

    protected override string LockNamePrefix => "payments-outbox";

    protected override BusinessMetrics.StoreName StoreName => BusinessMetrics.StoreName.PaymentsOutbox;
}
