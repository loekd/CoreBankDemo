using System.Diagnostics;
using System.Text.Json;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.PaymentsAPI.Inbox;

public class InboxProcessor(
    IServiceProvider serviceProvider,
    ILogger<InboxProcessor> logger,
    IDistributedLockService lockService,
    IInboxMessageRepository messageRepository,
    IOptions<InboxProcessingOptions> options)
    : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new(nameof(InboxProcessor));
    private readonly InboxProcessingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Payments Inbox Processor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPartitions(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing inbox partitions");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessPartitions(CancellationToken cancellationToken)
    {
        var partitionCount = _options.PartitionCount;
        logger.LogInformation("Processing {PartitionCount} inbox partitions", partitionCount);

        var tasks = Enumerable.Range(0, partitionCount)
            .Select(partitionId => ProcessPartition(partitionId, cancellationToken))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    private async Task ProcessPartition(int partitionId, CancellationToken cancellationToken)
    {
        var lockName = $"payments-inbox-partition-{partitionId}";

        await lockService.ExecuteWithLockAsync(
            lockName,
            _options.LockExpirySeconds,
            async ct => await ProcessPartitionMessages(partitionId, ct),
            cancellationToken);
    }

    private async Task ProcessPartitionMessages(int partitionId, CancellationToken cancellationToken)
    {
        var pendingMessageIds = await messageRepository.GetPendingMessageIdsForPartitionAsync(
            partitionId, cancellationToken);

        if (pendingMessageIds.Count == 0)
            return;

        logger.LogInformation("Processing {Count} pending inbox messages in partition {PartitionId}",
            pendingMessageIds.Count, partitionId);

        foreach (var messageId in pendingMessageIds)
        {
            await ProcessMessageAsync(messageId, cancellationToken);
        }
    }

    private async Task ProcessMessageAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var message = await messageRepository.LoadMessageAsync(messageId, cancellationToken);
        if (message == null)
            return;

        using var activity = CreateActivity(message);

        try
        {
            await messageRepository.MarkAsProcessingAsync(message, cancellationToken);

            using var scope = serviceProvider.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<ITransactionEventHandler>();

            await messageRepository.ExecuteInTransactionAsync(
                message,
                ct => DispatchEventAsync(handler, message, ct),
                cancellationToken);

            logger.LogInformation(
                "Successfully processed inbox message {MessageId} with idempotency key {IdempotencyKey}",
                message.Id, message.IdempotencyKey);
        }
        catch (Exception ex)
        {
            await messageRepository.MarkMessageAsFailedWithRetryAsync(
                messageId, ex.Message, cancellationToken);

            logger.LogWarning(ex, "Failed to process inbox message {MessageId}, retry count: {RetryCount}",
                message.Id, message.RetryCount);
        }
    }

    private static async Task DispatchEventAsync(
        ITransactionEventHandler handler,
        InboxMessage message,
        CancellationToken cancellationToken)
    {
        switch (message.EventType)
        {
            case nameof(TransactionCompletedEvent):
                var completed = JsonSerializer.Deserialize<TransactionCompletedEvent>(message.EventPayload)
                                ?? throw new InvalidOperationException("Failed to deserialize TransactionCompletedEvent");
                await handler.HandleAsync(completed, cancellationToken);
                break;

            case nameof(TransactionFailedEvent):
                var failed = JsonSerializer.Deserialize<TransactionFailedEvent>(message.EventPayload)
                             ?? throw new InvalidOperationException("Failed to deserialize TransactionFailedEvent");
                await handler.HandleAsync(failed, cancellationToken);
                break;

            case nameof(BalanceUpdatedEvent):
                var balanceUpdated = JsonSerializer.Deserialize<BalanceUpdatedEvent>(message.EventPayload)
                                     ?? throw new InvalidOperationException("Failed to deserialize BalanceUpdatedEvent");
                await handler.HandleAsync(balanceUpdated, cancellationToken);
                break;

            default:
                throw new InvalidOperationException($"Unknown event type: {message.EventType}");
        }
    }

    private static Activity? CreateActivity(InboxMessage message)
    {
        var hasParent = !string.IsNullOrWhiteSpace(message.TraceParent)
                        && ActivityContext.TryParse(message.TraceParent, message.TraceState, out var parentContext);

        var activity = hasParent
            ? ActivitySource.StartActivity("ProcessInboxMessage", ActivityKind.Consumer, parentContext)
            : ActivitySource.StartActivity("ProcessInboxMessage", ActivityKind.Consumer);

        activity?.SetTag("inbox.id", message.Id);
        activity?.SetTag("idempotency.key", message.IdempotencyKey);
        activity?.SetTag("event.type", message.EventType);
        return activity;
    }
}


