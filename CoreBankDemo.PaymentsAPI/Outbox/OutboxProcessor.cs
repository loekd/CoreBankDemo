using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.ServiceDefaults;
using Dapr.Client;

namespace CoreBankDemo.PaymentsAPI.Outbox;

public class OutboxProcessor(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    IDistributedLockService lockService,
    ILogger<OutboxProcessor> logger)
    : BackgroundService
{
    private readonly ActivitySource _activitySource = new("OutboxProcessor");
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);

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

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessPartitions(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var partitionCount = configuration.GetValue<int>("OutboxProcessing:PartitionCount");

        // Process all partitions in parallel
        logger.LogInformation("Processing {PartitionCount} partitions", partitionCount);
        var tasks = Enumerable.Range(0, partitionCount)
            .Select(async partitionId =>
            { 
                var lockName = $"outbox-partition-{partitionId}";
                return await lockService.ExecuteWithLockAsync(
                        lockName,
                        async ct => await ProcessPartitionMessages(partitionId, ct),
                        cancellationToken);   
                })
            .ToArray();

        await Task.WhenAll(tasks);
    }

    private async Task ProcessPartitionMessages(int partitionId, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var pendingMessageIds = await GetPendingMessageIdsForPartition(dbContext, partitionId, cancellationToken);

        if (pendingMessageIds.Count == 0)
            return;

        logger.LogInformation("Processing {Count} pending outbox messages in partition {PartitionId}",
            pendingMessageIds.Count, partitionId);

        foreach (var messageId in pendingMessageIds)
        {
            await ProcessMessage(messageId, cancellationToken);
        }
    }


    private async Task ProcessMessage(
        Guid messageId,
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

    private async Task<List<Guid>> GetPendingMessageIdsForPartition(
        PaymentsDbContext dbContext,
        int partitionId,
        CancellationToken cancellationToken)
    {
        var staleThreshold = timeProvider.GetUtcNow().Subtract(ProcessingTimeout).UtcDateTime;

        return await dbContext.OutboxMessages
            .Where(m => m.PartitionId == partitionId &&
                       m.RetryCount < 5 &&
                       (m.Status == "Pending" ||
                        (m.Status == "Processing" && m.CreatedAt < staleThreshold)))
            .OrderBy(m => m.CreatedAt)
            .Take(10)
            .Select(m => m.Id)
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

            if (validation.IsValid)
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
