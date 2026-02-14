using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace CoreBankDemo.PaymentsAPI;

public class OutboxProcessor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly ActivitySource _activitySource = new("OutboxProcessor");

    public OutboxProcessor(IServiceProvider serviceProvider, ILogger<OutboxProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox Processor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingMessages(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing outbox messages");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessPendingMessages(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var useOrdering = configuration.GetValue<bool>("Features:UseOrdering");

        List<OutboxMessage> pendingMessages;

        if (useOrdering)
        {
            // Get distinct partition keys with pending messages
            var partitions = await dbContext.OutboxMessages
                .Where(m => m.Status == "Pending" && m.RetryCount < 5)
                .Select(m => m.PartitionKey)
                .Distinct()
                .Take(10)
                .ToListAsync(cancellationToken);

            // Get the oldest message from each partition (ensures ordering within partition)
            pendingMessages = new List<OutboxMessage>();
            foreach (var partition in partitions)
            {
                var message = await dbContext.OutboxMessages
                    .Where(m => m.PartitionKey == partition && m.Status == "Pending" && m.RetryCount < 5)
                    .OrderBy(m => m.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (message != null)
                    pendingMessages.Add(message);
            }
        }
        else
        {
            // Process all pending messages without ordering
            pendingMessages = await dbContext.OutboxMessages
                .Where(m => m.Status == "Pending" && m.RetryCount < 5)
                .OrderBy(m => m.CreatedAt)
                .Take(10)
                .ToListAsync(cancellationToken);
        }

        if (pendingMessages.Count == 0)
            return;

        _logger.LogInformation("Processing {Count} pending outbox messages (Ordering: {UseOrdering})", 
            pendingMessages.Count, useOrdering);

        foreach (var message in pendingMessages)
        {
            using var activity = _activitySource.StartActivity("ProcessOutboxMessage");
            activity?.SetTag("outbox.id", message.Id);
            activity?.SetTag("payment.id", message.PaymentId);

            try
            {
                message.Status = "Processing";
                await dbContext.SaveChangesAsync(cancellationToken);

                var client = httpClientFactory.CreateClient("CoreBank");
                var coreBankUrl = configuration["CoreBankApi:BaseUrl"] ?? "http://localhost:5032";

                // Validate account
                var validationResponse = await client.PostAsJsonAsync(
                    $"{coreBankUrl}/api/accounts/validate",
                    new { AccountNumber = message.ToAccount },
                    cancellationToken);

                validationResponse.EnsureSuccessStatusCode();
                var validation = await validationResponse.Content.ReadFromJsonAsync<AccountValidationResponse>(cancellationToken);

                if (validation?.IsValid != true)
                {
                    message.Status = "Failed";
                    message.LastError = "Invalid account number";
                    await dbContext.SaveChangesAsync(cancellationToken);
                    continue;
                }

                // Process transaction
                var transactionResponse = await client.PostAsJsonAsync(
                    $"{coreBankUrl}/api/transactions/process",
                    new
                    {
                        FromAccount = message.FromAccount,
                        ToAccount = message.ToAccount,
                        Amount = message.Amount,
                        Currency = message.Currency,
                        IdempotencyKey = message.PaymentId // Use PaymentId as idempotency key
                    },
                    cancellationToken);

                transactionResponse.EnsureSuccessStatusCode();

                message.Status = "Completed";
                message.ProcessedAt = DateTimeOffset.UtcNow;
                _logger.LogInformation("Successfully processed outbox message {MessageId} for payment {PaymentId}",
                    message.Id, message.PaymentId);
            }
            catch (Exception ex)
            {
                message.Status = "Pending";
                message.RetryCount++;
                message.LastError = ex.Message;
                _logger.LogWarning(ex, "Failed to process outbox message {MessageId}, retry count: {RetryCount}",
                    message.Id, message.RetryCount);
            }
            finally
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
    }
}
