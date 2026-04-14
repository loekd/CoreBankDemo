using System.Diagnostics;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static CoreBankDemo.Messaging.MessageConstants;

namespace CoreBankDemo.Messaging.Outbox;

/// <summary>
/// Base background service for processing outbox messages with partitioning, locking, and distributed tracing.
/// </summary>
public abstract class OutboxProcessorBase<TMessage, TDbContext> : BackgroundService
    where TMessage : class, IOutboxMessage
    where TDbContext : DbContext
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;
    private readonly IDistributedLockService _lockService;
    private readonly OutboxProcessingOptions _options;
    private readonly ActivitySource _activitySource;
    protected readonly TimeProvider TimeProvider;

    protected OutboxProcessorBase(
        IServiceProvider serviceProvider,
        ILogger logger,
        IDistributedLockService lockService,
        TimeProvider timeProvider,
        IOptions<OutboxProcessingOptions> options,
        string activitySourceName)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _lockService = lockService;
        TimeProvider = timeProvider;
        _options = options.Value;
        _activitySource = new ActivitySource(activitySourceName);
    }

    /// <summary>
    /// Override to provide the lock name prefix for partitions (e.g., "outbox", "messaging-outbox").
    /// </summary>
    protected abstract string LockNamePrefix { get; }

    /// <summary>
    /// Override to implement domain-specific message processing logic.
    /// </summary>
    protected abstract Task ProcessMessageAsync(
        TMessage message,
        IServiceProvider scopedServiceProvider,
        CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{ProcessorName} started", GetType().Name);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPartitions(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing outbox partitions");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(_options.PollingIntervalMs), stoppingToken);
        }
    }

    private async Task ProcessPartitions(CancellationToken cancellationToken)
    {
        var partitionCount = _options.PartitionCount;
        _logger.LogInformation("Processing {PartitionCount} outbox partitions", partitionCount);

        var tasks = Enumerable.Range(0, partitionCount)
            .Select(partitionId => ProcessPartition(partitionId, cancellationToken))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    private async Task ProcessPartition(int partitionId, CancellationToken cancellationToken)
    {
        var lockName = $"{LockNamePrefix}-partition-{partitionId}";

        await _lockService.ExecuteWithLockAsync(
            lockName,
            _options.LockExpirySeconds,
            async ct => await ProcessPartitionMessages(partitionId, ct),
            cancellationToken);
    }

    private async Task ProcessPartitionMessages(int partitionId, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<OutboxMessageRepositoryBase<TMessage, TDbContext>>();

        var pendingMessageIds = await repository.GetPendingMessageIdsForPartitionAsync(
            partitionId, cancellationToken);

        if (pendingMessageIds.Count == 0)
            return;

        _logger.LogInformation("Processing {Count} pending outbox messages in partition {PartitionId}",
            pendingMessageIds.Count, partitionId);

        foreach (var messageId in pendingMessageIds)
        {
            await ProcessMessageWithHandlingAsync(messageId, cancellationToken);
        }
    }

    private async Task ProcessMessageWithHandlingAsync(Guid messageId, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<OutboxMessageRepositoryBase<TMessage, TDbContext>>();

        var message = await repository.LoadMessageAsync(messageId, cancellationToken);
        if (message == null)
            return;

        using var activity = CreateActivity(message);

        try
        {
            await ProcessMessageAsync(message, scope.ServiceProvider, cancellationToken);
        }
        catch (Exception ex)
        {
            await repository.MarkAsFailedWithRetryAsync(message, ex.Message, cancellationToken);

            _logger.LogWarning(ex, "Failed to process outbox message {MessageId}, retry count: {RetryCount}",
                message.Id, message.RetryCount);
        }
    }

    protected Activity? CreateActivity(TMessage message)
    {
        var hasParent = !string.IsNullOrWhiteSpace(message.TraceParent)
                        && ActivityContext.TryParse(message.TraceParent, message.TraceState, out var parentContext);

        var activity = hasParent
            ? _activitySource.StartActivity("ProcessOutboxMessage", ActivityKind.Producer, parentContext)
            : _activitySource.StartActivity("ProcessOutboxMessage", ActivityKind.Producer);

        activity?.SetTag("outbox.id", message.Id);
        activity?.SetTag("idempotency.key", message.IdempotencyKey);
        activity?.SetTag("queue_duration_ms", (long)(TimeProvider.GetUtcNow().UtcDateTime - message.CreatedAt).TotalMilliseconds);

        return activity;
    }

    protected TService GetService<TService>(IServiceProvider scopedServiceProvider)
        where TService : notnull
    {
        return scopedServiceProvider.GetRequiredService<TService>();
    }
}
