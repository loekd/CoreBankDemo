using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using CoreBankDemo.PaymentsAPI.Models;

namespace CoreBankDemo.PaymentsAPI;

public class OutboxProcessor(
    IServiceProvider serviceProvider,
    ILogger<OutboxProcessor> logger,
    TimeProvider timeProvider)
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
                await ProcessPendingMessages(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing outbox messages");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessPendingMessages(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var pendingMessages = await GetPendingMessages(dbContext, cancellationToken);

        if (pendingMessages.Count == 0)
            return;

        logger.LogInformation("Processing {Count} pending outbox messages", pendingMessages.Count);

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
            var client = httpClientFactory.CreateClient("CoreBank");
            var coreBankUrl = configuration["CoreBankApi:BaseUrl"] ?? "http://localhost:5032";

            // Validate account (transient operation - no DB save)
            var validationResult = await ValidateAccount(message, client, coreBankUrl, cancellationToken);

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
            await ProcessTransaction(message, client, coreBankUrl, cancellationToken);

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

    private static async Task<List<OutboxMessage>> GetPendingMessages(
        PaymentsDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var staleThreshold = DateTime.UtcNow.Subtract(ProcessingTimeout);

        return await dbContext.OutboxMessages
            .Where(m => m.RetryCount < 5 &&
                       (m.Status == "Pending" ||
                        (m.Status == "Processing" && m.CreatedAt < staleThreshold)))
            .OrderBy(m => m.CreatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);
    }

    private static async Task<ValidationResult> ValidateAccount(
        OutboxMessage message,
        HttpClient client,
        string coreBankUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var validationResponse = await client.PostAsJsonAsync(
                $"{coreBankUrl}/api/accounts/validate",
                new { AccountNumber = message.ToAccount },
                cancellationToken);

            validationResponse.EnsureSuccessStatusCode();
            var validation = await validationResponse.Content
                .ReadFromJsonAsync<AccountValidationResponse>(cancellationToken);

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
        HttpClient client,
        string coreBankUrl,
        CancellationToken cancellationToken)
    {
        var transactionResponse = await client.PostAsJsonAsync(
            $"{coreBankUrl}/api/transactions/process",
            new
            {
                FromAccount = message.FromAccount,
                ToAccount = message.ToAccount,
                Amount = message.Amount,
                Currency = message.Currency,
                IdempotencyKey = message.PaymentId
            },
            cancellationToken);

        transactionResponse.EnsureSuccessStatusCode();
    }

    private record ValidationResult(bool IsValid, string? Error)
    {
        public static ValidationResult Success() => new(true, null);
        public static ValidationResult Failure(string error) => new(false, error);
    }
}
