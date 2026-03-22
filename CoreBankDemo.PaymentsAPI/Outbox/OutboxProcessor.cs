using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;
using static CoreBankDemo.Messaging.MessageConstants;

namespace CoreBankDemo.PaymentsAPI.Outbox;

public class OutboxProcessor(
    IServiceProvider serviceProvider,
    IDistributedLockService lockService,
    IOutboxMessageHandler messageProcessor,
    IOptions<OutboxProcessingOptions> options,
    ILogger<OutboxProcessor> logger)
    : BackgroundService
{
    private readonly OutboxProcessingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox Processor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPartitions(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing outbox partitions");
            }

            await Task.Delay(Defaults.PollingInterval, stoppingToken);
        }
    }

    private async Task ProcessPartitions(CancellationToken cancellationToken)
    {
        var partitionCount = _options.PartitionCount;
        logger.LogInformation("Processing {PartitionCount} partitions", partitionCount);

        var tasks = Enumerable.Range(0, partitionCount)
            .Select(partitionId => ProcessPartition(partitionId, cancellationToken))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    private async Task ProcessPartition(int partitionId, CancellationToken cancellationToken)
    {
        var lockName = $"outbox-partition-{partitionId}";

        await lockService.ExecuteWithLockAsync(
            lockName,
            _options.LockExpirySeconds,
            async ct => await ProcessPartitionMessages(partitionId, ct),
            cancellationToken);
    }

    private async Task ProcessPartitionMessages(int partitionId, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var pendingMessageIds = await messageProcessor.GetPendingMessageIdsForPartitionAsync(
            dbContext, partitionId, cancellationToken);

        if (pendingMessageIds.Count == 0)
            return;

        logger.LogInformation("Processing {Count} pending outbox messages in partition {PartitionId}",
            pendingMessageIds.Count, partitionId);

        foreach (var messageId in pendingMessageIds)
        {
            await messageProcessor.ProcessMessageAsync(messageId, cancellationToken);
        }
    }
}
