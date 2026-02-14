using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;

namespace CoreBankDemo.CoreBankAPI;

public class InboxProcessor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InboxProcessor> _logger;
    private readonly ActivitySource _activitySource = new("InboxProcessor");

    public InboxProcessor(IServiceProvider serviceProvider, ILogger<InboxProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Inbox Processor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingMessages(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing inbox messages");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessPendingMessages(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();

        var pendingMessages = await dbContext.InboxMessages
            .Where(m => m.Status == "Pending" && m.RetryCount < 5)
            .OrderBy(m => m.ReceivedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        if (pendingMessages.Count == 0)
            return;

        _logger.LogInformation("Processing {Count} pending inbox messages", pendingMessages.Count);

        foreach (var message in pendingMessages)
        {
            using var activity = _activitySource.StartActivity("ProcessInboxMessage");
            activity?.SetTag("inbox.id", message.Id);
            activity?.SetTag("idempotency.key", message.IdempotencyKey);

            try
            {
                message.Status = "Processing";
                await dbContext.SaveChangesAsync(cancellationToken);

                // Simulate transaction processing in legacy core bank
                var transactionId = Guid.NewGuid().ToString();
                var response = new TransactionResponse(
                    transactionId,
                    "Completed",
                    DateTimeOffset.UtcNow
                );

                message.TransactionId = transactionId;
                message.Status = "Completed";
                message.ProcessedAt = DateTimeOffset.UtcNow;
                message.ResponsePayload = JsonSerializer.Serialize(response);

                _logger.LogInformation(
                    "Successfully processed inbox message {MessageId} with idempotency key {IdempotencyKey}, transaction {TransactionId}",
                    message.Id, message.IdempotencyKey, transactionId);
            }
            catch (Exception ex)
            {
                message.Status = "Pending";
                message.RetryCount++;
                message.LastError = ex.Message;
                _logger.LogWarning(ex, "Failed to process inbox message {MessageId}, retry count: {RetryCount}",
                    message.Id, message.RetryCount);
            }
            finally
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
    }
}
