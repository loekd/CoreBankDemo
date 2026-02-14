using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Models;

namespace CoreBankDemo.CoreBankAPI;

public class InboxProcessor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InboxProcessor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ActivitySource _activitySource = new("InboxProcessor");

    public InboxProcessor(IServiceProvider serviceProvider, ILogger<InboxProcessor> logger, TimeProvider timeProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _timeProvider = timeProvider;
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

                // Get accounts
                var fromAccount = await dbContext.Accounts
                    .FirstOrDefaultAsync(a => a.AccountNumber == message.FromAccount, cancellationToken);
                
                var toAccount = await dbContext.Accounts
                    .FirstOrDefaultAsync(a => a.AccountNumber == message.ToAccount, cancellationToken);

                // Re-validate (defensive check)
                if (fromAccount == null || !fromAccount.IsActive)
                {
                    message.Status = "Failed";
                    message.LastError = $"Source account {message.FromAccount} not found or inactive";
                    _logger.LogWarning("Transaction {IdempotencyKey} failed: {Error}", 
                        message.IdempotencyKey, message.LastError);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    continue;
                }

                if (toAccount == null || !toAccount.IsActive)
                {
                    message.Status = "Failed";
                    message.LastError = $"Destination account {message.ToAccount} not found or inactive";
                    _logger.LogWarning("Transaction {IdempotencyKey} failed: {Error}", 
                        message.IdempotencyKey, message.LastError);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    continue;
                }

                if (fromAccount.Balance < message.Amount)
                {
                    message.Status = "Failed";
                    message.LastError = $"Insufficient funds. Available: {fromAccount.Balance}, Required: {message.Amount}";
                    _logger.LogWarning("Transaction {IdempotencyKey} failed: {Error}", 
                        message.IdempotencyKey, message.LastError);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    continue;
                }

                // Process transaction: deduct from source, credit to destination
                fromAccount.Balance -= message.Amount;
                fromAccount.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

                toAccount.Balance += message.Amount;
                toAccount.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

                var transactionId = Guid.NewGuid().ToString();
                var processedAt = _timeProvider.GetUtcNow();
                var response = new TransactionResponse(
                    transactionId,
                    "Completed",
                    processedAt
                );

                message.TransactionId = transactionId;
                message.Status = "Completed";
                message.ProcessedAt = processedAt.UtcDateTime;
                message.ResponsePayload = JsonSerializer.Serialize(response);

                _logger.LogInformation(
                    "Successfully processed inbox message {MessageId} with idempotency key {IdempotencyKey}, transaction {TransactionId}. " +
                    "Transferred {Amount} {Currency} from {FromAccount} to {ToAccount}",
                    message.Id, message.IdempotencyKey, transactionId, message.Amount, message.Currency, 
                    message.FromAccount, message.ToAccount);
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
