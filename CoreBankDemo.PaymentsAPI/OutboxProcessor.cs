using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using CoreBankDemo.PaymentsAPI.Models;
using Dapr.Client;

namespace CoreBankDemo.PaymentsAPI;

public class OutboxProcessor(
    IServiceProvider serviceProvider,
    ILogger<OutboxProcessor> logger,
    TimeProvider timeProvider)
    : BackgroundService
{
    private readonly ActivitySource _activitySource = new("OutboxProcessor");
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);
    private readonly string _instanceId = Guid.NewGuid().ToString();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox Processor started with instance ID: {InstanceId}", _instanceId);

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

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessPartitions(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var daprClient = scope.ServiceProvider.GetRequiredService<DaprClient>();

        var partitionCount = configuration.GetValue<int>("OutboxProcessing:PartitionCount");
        var lockExpirySeconds = configuration.GetValue<int>("OutboxProcessing:LockExpirySeconds");

        // Try to acquire lock for each partition
        for (int partitionId = 0; partitionId < partitionCount; partitionId++)
        {
            var lockOwner = $"{_instanceId}-partition-{partitionId}";
            var lockName = $"outbox-partition-{partitionId}";

            try
            {
#pragma warning disable DAPR_DISTRIBUTEDLOCK
                
                // Try to acquire lock
                var lockResponse = await daprClient.Lock(
                    "lockstore",
                    lockName,
                    lockOwner,
                    lockExpirySeconds,
                    cancellationToken);

                if (lockResponse.Success)
                {
                    logger.LogInformation("Acquired lock for partition {PartitionId}", partitionId);

                    try
                    {
                        await ProcessPartitionMessages(partitionId, cancellationToken);
                    }
                    finally
                    {
                        // Release lock
                        await daprClient.Unlock("lockstore", lockName, lockOwner, cancellationToken);
                        logger.LogInformation("Released lock for partition {PartitionId}", partitionId);
                    }
#pragma warning restore DAPR_DISTRIBUTEDLOCK
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to acquire or process partition {PartitionId}", partitionId);
            }
        }
    }

    private async Task ProcessPartitionMessages(int partitionId, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var pendingMessages = await GetPendingMessagesForPartition(dbContext, partitionId, cancellationToken);

        if (pendingMessages.Count == 0)
            return;

        logger.LogInformation("Processing {Count} pending outbox messages in partition {PartitionId}",
            pendingMessages.Count, partitionId);

        foreach (var message in pendingMessages)
        {
            // Process each message in its own DB context to ensure isolation
            await ProcessMessageIsolated(message.Id, httpClientFactory, configuration, cancellationToken);
        }
    }


    private async Task ProcessMessageIsolated(
        Guid messageId,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var daprClient = scope.ServiceProvider.GetRequiredService<DaprClient>();

        // Reload message in fresh context with row-level locking
        var message = await dbContext.OutboxMessages
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);

        if (message == null)
            return;

        using var activity = _activitySource.StartActivity("ProcessOutboxMessage");
        activity?.SetTag("outbox.id", message.Id);
        activity?.SetTag("payment.id", message.PaymentId);

        try
        {
            // Validate account (transient operation - no DB save)
            var validationResult = await ValidateAccount(message, daprClient, cancellationToken);

            if (!validationResult.IsValid)
            {
                // Permanent failure - mark as failed
                message.Status = "Failed";
                message.LastError = validationResult.Error;
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogWarning("Outbox message {MessageId} failed validation: {Error}",
                    message.Id, validationResult.Error);
                return;
            }

            // Process transaction (transient operation - no DB save)
            await ProcessTransaction(message, daprClient, cancellationToken);

            // SUCCESS - mark as completed (single save)
            message.Status = "Completed";
            message.ProcessedAt = timeProvider.GetUtcNow().UtcDateTime;
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Successfully processed outbox message {MessageId} for payment {PaymentId}",
                message.Id, message.PaymentId);
        }
        catch (Exception ex)
        {
            // TRANSIENT ERROR - increment retry, reset to Pending (single save)
            message.Status = "Pending";
            message.RetryCount++;
            message.LastError = ex.Message;
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogWarning(ex, "Failed to process outbox message {MessageId}, retry count: {RetryCount}",
                message.Id, message.RetryCount);
        }
    }

    private static async Task<List<OutboxMessage>> GetPendingMessagesForPartition(
        PaymentsDbContext dbContext,
        int partitionId,
        CancellationToken cancellationToken)
    {
        var staleThreshold = DateTime.UtcNow.Subtract(ProcessingTimeout);

        return await dbContext.OutboxMessages
            .Where(m => m.PartitionId == partitionId &&
                       m.RetryCount < 5 &&
                       (m.Status == "Pending" ||
                        (m.Status == "Processing" && m.CreatedAt < staleThreshold)))
            .OrderBy(m => m.CreatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);
    }

    private static async Task<ValidationResult> ValidateAccount(
        OutboxMessage message,
        DaprClient daprClient,
        CancellationToken cancellationToken)
    {
        try
        {
            var validation = await daprClient.InvokeMethodAsync<object, AccountValidationResponse>(
                "corebank-api",
                "api/accounts/validate",
                new { AccountNumber = message.ToAccount },
                cancellationToken);

            if (validation?.IsValid == true)
                return ValidationResult.Success();

            return ValidationResult.Failure("Invalid account number");
        }
        catch (HttpRequestException)
        {
            // Network errors are transient - let them bubble up
            throw;
        }
    }

    private static async Task ProcessTransaction(
        OutboxMessage message,
        DaprClient daprClient,
        CancellationToken cancellationToken)
    {
        await daprClient.InvokeMethodAsync(
            "corebank-api",
            "api/transactions/process",
            new
            {
                FromAccount = message.FromAccount,
                ToAccount = message.ToAccount,
                Amount = message.Amount,
                Currency = message.Currency,
                IdempotencyKey = message.PaymentId
            },
            cancellationToken);
    }

    private record ValidationResult(bool IsValid, string? Error)
    {
        public static ValidationResult Success() => new(true, null);
        public static ValidationResult Failure(string error) => new(false, error);
    }
}
