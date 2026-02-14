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
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);

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

        var pendingMessages = await GetPendingMessages(dbContext, cancellationToken);

        if (pendingMessages.Count == 0)
            return;

        _logger.LogInformation("Processing {Count} pending inbox messages", pendingMessages.Count);

        foreach (var message in pendingMessages)
        {
            // Process each message in its own DB context to ensure isolation
            await ProcessMessageIsolated(message.Id, cancellationToken);
        }
    }

    private async Task ProcessMessageIsolated(Guid messageId, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();

        // Use execution strategy for transient failures
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            // Use transaction for atomicity of account updates + message status
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                // Reload message in fresh context
                var message = await dbContext.InboxMessages
                    .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);

                if (message == null)
                    return;

                using var activity = _activitySource.StartActivity("ProcessInboxMessage");
                activity?.SetTag("inbox.id", message.Id);
                activity?.SetTag("idempotency.key", message.IdempotencyKey);

                // Load accounts
                var fromAccount = await dbContext.Accounts
                    .FirstOrDefaultAsync(a => a.AccountNumber == message.FromAccount, cancellationToken);

                var toAccount = await dbContext.Accounts
                    .FirstOrDefaultAsync(a => a.AccountNumber == message.ToAccount, cancellationToken);

                // Validate (no DB save yet)
                var validationResult = ValidateTransaction(message, fromAccount, toAccount);

                if (!validationResult.IsValid)
                {
                    // Permanent failure - mark as failed
                    message.Status = "Failed";
                    message.LastError = validationResult.Error;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    _logger.LogWarning("Transaction {IdempotencyKey} failed validation: {Error}",
                        message.IdempotencyKey, validationResult.Error);
                    return;
                }

                // Execute transaction (update accounts + message status atomically)
                fromAccount!.Balance -= message.Amount;
                fromAccount.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

                toAccount!.Balance += message.Amount;
                toAccount.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

                var transactionId = Guid.NewGuid().ToString();
                var processedAt = _timeProvider.GetUtcNow();
                var response = new TransactionResponse(transactionId, "Completed", processedAt);

                message.TransactionId = transactionId;
                message.Status = "Completed";
                message.ProcessedAt = processedAt.UtcDateTime;
                message.ResponsePayload = JsonSerializer.Serialize(response);

                // Single SaveChanges - all or nothing
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation(
                    "Successfully processed inbox message {MessageId} with idempotency key {IdempotencyKey}, transaction {TransactionId}. " +
                    "Transferred {Amount} {Currency} from {FromAccount} to {ToAccount}",
                    message.Id, message.IdempotencyKey, transactionId, message.Amount, message.Currency,
                    message.FromAccount, message.ToAccount);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);

                // Reload message to update retry count (outside transaction)
                var message = await dbContext.InboxMessages
                    .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);

                if (message != null)
                {
                    message.Status = "Pending";
                    message.RetryCount++;
                    message.LastError = ex.Message;
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                _logger.LogWarning(ex, "Failed to process inbox message {MessageId}, retry count: {RetryCount}",
                    messageId, message?.RetryCount ?? 0);
            }
        });
    }

    private static async Task<List<InboxMessage>> GetPendingMessages(
        CoreBankDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var staleThreshold = DateTime.UtcNow.Subtract(ProcessingTimeout);

        return await dbContext.InboxMessages
            .Where(m => m.RetryCount < 5 &&
                       (m.Status == "Pending" ||
                        (m.Status == "Processing" && m.ReceivedAt < staleThreshold)))
            .OrderBy(m => m.ReceivedAt)
            .Take(10)
            .ToListAsync(cancellationToken);
    }

    private ValidationResult ValidateTransaction(
        InboxMessage message,
        Account? fromAccount,
        Account? toAccount)
    {
        if (fromAccount == null || !fromAccount.IsActive)
            return ValidationResult.Failure($"Source account {message.FromAccount} not found or inactive");

        if (toAccount == null || !toAccount.IsActive)
            return ValidationResult.Failure($"Destination account {message.ToAccount} not found or inactive");

        if (fromAccount.Balance < message.Amount)
            return ValidationResult.Failure($"Insufficient funds. Available: {fromAccount.Balance}, Required: {message.Amount}");

        return ValidationResult.Success();
    }

    private record ValidationResult(bool IsValid, string? Error)
    {
        public static ValidationResult Success() => new(true, null);
        public static ValidationResult Failure(string error) => new(false, error);
    }
}
