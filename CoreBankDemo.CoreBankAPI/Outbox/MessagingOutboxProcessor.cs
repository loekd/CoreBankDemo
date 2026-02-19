using System.Diagnostics;
using System.Net.Mime;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Dapr.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.CoreBankAPI.Outbox;

public class MessagingOutboxProcessor(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    IDistributedLockService lockService,
    IOptions<MessagingOutboxProcessingOptions> options,
    ILogger<MessagingOutboxProcessor> logger)
    : BackgroundService
{
    private readonly ActivitySource _activitySource = new("MessagingOutboxProcessor");
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);
    private readonly MessagingOutboxProcessingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Messaging Outbox Processor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPartitions(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing messaging outbox partitions");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessPartitions(CancellationToken cancellationToken)
    {
        var partitionCount = _options.PartitionCount;

        // Process all partitions in parallel
        logger.LogInformation("Processing {PartitionCount} messaging outbox partitions", partitionCount);
        var tasks = Enumerable.Range(0, partitionCount)
            .Select(async partitionId =>
            {
                var lockName = $"messaging-outbox-partition-{partitionId}";
                return await lockService.ExecuteWithLockAsync(
                    lockName,
                    _options.LockExpirySeconds,
                    async ct => await ProcessPartitionMessages(partitionId, ct),
                    cancellationToken);
            })
            .ToArray();

        await Task.WhenAll(tasks);
    }

    private async Task ProcessPartitionMessages(int partitionId, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();

        var pendingMessageIds = await GetPendingMessageIdsForPartition(dbContext, partitionId, cancellationToken);

        if (pendingMessageIds.Count == 0)
            return;

        logger.LogInformation("Processing {Count} pending messaging outbox messages in partition {PartitionId}",
            pendingMessageIds.Count, partitionId);

        foreach (var messageId in pendingMessageIds)
        {
            await ProcessMessage(messageId, cancellationToken);
        }
    }

    private async Task ProcessMessage(Guid messageId, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();
        var daprClient = scope.ServiceProvider.GetRequiredService<DaprClient>();

        var message = await LoadMessage(dbContext, messageId, cancellationToken);
        if (message == null)
            return;

        using var activity = CreateActivity(message);

        try
        {
            await PublishMessageAsCloudEvent(daprClient, message, cancellationToken);
            await MarkMessageAsCompleted(dbContext, message, cancellationToken);

            LogSuccess(message);
        }
        catch (Exception ex)
        {
            await HandlePublishFailure(dbContext, message, ex, cancellationToken);
        }
    }

    private static async Task<MessagingOutboxMessage?> LoadMessage(
        CoreBankDbContext dbContext,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        return await dbContext.MessagingOutboxMessages
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
    }

    private Activity? CreateActivity(MessagingOutboxMessage message)
    {
        ActivityContext parentContext;
        var hasParent = !string.IsNullOrWhiteSpace(message.TraceParent)
                        && ActivityContext.TryParse(message.TraceParent, message.TraceState, out parentContext);

        var activity = hasParent
            ? _activitySource.StartActivity("ProcessMessagingOutboxMessage", ActivityKind.Producer, parentContext)
            : _activitySource.StartActivity("ProcessMessagingOutboxMessage", ActivityKind.Producer);

        activity?.SetTag("outbox.id", message.Id);
        activity?.SetTag("transaction.id", message.TransactionId);
        activity?.SetTag("event.type", message.EventType);
        return activity;
    }

    private async Task PublishMessageAsCloudEvent(
        DaprClient daprClient,
        MessagingOutboxMessage message,
        CancellationToken cancellationToken)
    {
        var payload = new TransactionResponse(message.TransactionId,
            message.TransactionStatus,
            message.ProcessedAt ?? message.CreatedAt);

        // Build CloudEvent as a dictionary to avoid serialization issues with CloudNative.CloudEvents
        var cloudEvent = new Dictionary<string, object>
        {
            ["id"] = message.Id.ToString(),
            ["specversion"] = "1.0",
            ["type"] = message.EventType,
            ["source"] = message.EventSource,
            ["subject"] = $"transaction/{message.TransactionId}",
            ["datacontenttype"] = MediaTypeNames.Application.Json,
            ["data"] = payload
        };

        var traceParent = Activity.Current?.Id ?? message.TraceParent;
        var traceState = Activity.Current?.TraceStateString ?? message.TraceState;

        if (!string.IsNullOrWhiteSpace(traceParent))
        {
            cloudEvent["traceparent"] = traceParent;
        }

        if (!string.IsNullOrWhiteSpace(traceState))
        {
            cloudEvent["tracestate"] = traceState;
        }

        // Publish the CloudEvent directly through Dapr with rawPayload to preserve CloudEvent structure
        var metadata = new Dictionary<string, string>
        {
            ["rawPayload"] = "true"
        };

        await daprClient.PublishEventAsync(
            _options.PubSubName,
            _options.TopicName,
            cloudEvent, metadata, cancellationToken);
    }

    private async Task MarkMessageAsCompleted(
        CoreBankDbContext dbContext,
        MessagingOutboxMessage message,
        CancellationToken cancellationToken)
    {
        message.Status = "Completed";
        message.ProcessedAt = timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task HandlePublishFailure(
        CoreBankDbContext dbContext,
        MessagingOutboxMessage message,
        Exception ex,
        CancellationToken cancellationToken)
    {
        message.Status = "Pending";
        message.RetryCount++;
        message.LastError = ex.Message;
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogWarning(ex,
            "Failed to publish CloudEvent for messaging outbox message {MessageId}, retry count: {RetryCount}",
            message.Id, message.RetryCount);
    }

    private void LogSuccess(MessagingOutboxMessage message)
    {
        logger.LogInformation(
            "Successfully published CloudEvent for messaging outbox message {MessageId}, transaction {TransactionId}",
            message.Id, message.TransactionId);
    }
    

    private async Task<List<Guid>> GetPendingMessageIdsForPartition(
        CoreBankDbContext dbContext,
        int partitionId,
        CancellationToken cancellationToken)
    {
        var staleThreshold = timeProvider.GetUtcNow().Subtract(ProcessingTimeout).UtcDateTime;

        return await dbContext.MessagingOutboxMessages
            .Where(m => m.PartitionId == partitionId &&
                       m.RetryCount < 5 &&
                       (m.Status == "Pending" ||
                        (m.Status == "Processing" && m.CreatedAt < staleThreshold)))
            .OrderBy(m => m.CreatedAt)
            .Take(10)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);
    }
}
