using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using static CoreBankDemo.Messaging.MessageConstants;

namespace CoreBankDemo.PaymentsAPI.Outbox;

public class OutboxMessageHandler(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    ICoreBankApiClient coreBankApiClient,
    ILogger<OutboxMessageHandler> logger) : IOutboxMessageHandler
{
    private readonly ActivitySource _activitySource = new("OutboxProcessor");

    public async Task<List<Guid>> GetPendingMessageIdsForPartitionAsync(
        PaymentsDbContext dbContext,
        int partitionId,
        CancellationToken cancellationToken)
    {
        var staleThreshold = timeProvider.GetUtcNow().Subtract(Defaults.ProcessingTimeout).UtcDateTime;

        return await dbContext.OutboxMessages
            .Where(m => m.PartitionId == partitionId &&
                        m.RetryCount < Defaults.MaxRetryCount &&
                        (m.Status == Status.Pending ||
                         (m.Status == Status.Processing && m.CreatedAt < staleThreshold)))
            .OrderBy(m => m.CreatedAt)
            .Take(Defaults.BatchSize)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task ProcessMessageAsync(Guid messageId, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var message = await dbContext.OutboxMessages
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);

        if (message == null)
            return;

        using var activity = CreateActivity(message);

        try
        {
            var validationResult = await ValidateAccountAsync(message, cancellationToken);

            if (!validationResult.IsValid)
            {
                message.Status = Status.Failed;
                message.LastError = validationResult.Error;
                await dbContext.SaveChangesAsync(cancellationToken);

                logger.LogWarning("Outbox message {MessageId} failed validation: {Error}",
                    message.Id, validationResult.Error);
                return;
            }

            await coreBankApiClient.ProcessTransactionAsync(message, cancellationToken);

            message.Status = Status.Completed;
            message.ProcessedAt = timeProvider.GetUtcNow().UtcDateTime;
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Successfully processed outbox message {MessageId} for payment {PaymentId}",
                message.Id, message.TransactionId);
        }
        catch (Exception ex)
        {
            message.Status = Status.Pending;
            message.RetryCount++;
            message.LastError = ex.Message;
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogWarning(ex, "Failed to process outbox message {MessageId}, retry count: {RetryCount}",
                message.Id, message.RetryCount);
        }
    }

    private async Task<ValidationResult> ValidateAccountAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var response = await coreBankApiClient.ValidateAccountAsync(message.ToAccount, cancellationToken);

        return response.IsValid
            ? ValidationResult.Success()
            : ValidationResult.Failure("Invalid account number");
    }

    private Activity? CreateActivity(OutboxMessage message)
    {
        var hasParent = !string.IsNullOrWhiteSpace(message.TraceParent)
                        && ActivityContext.TryParse(message.TraceParent, message.TraceState, out var parentContext);

        var activity = _activitySource.StartActivity(
            "ProcessOutboxMessage",
            ActivityKind.Producer,
            hasParent ? parentContext : default);

        activity?.SetTag("outbox.id", message.Id);
        activity?.SetTag("payment.id", message.TransactionId);
        activity?.SetTag("queue_duration_ms",
            (long)(timeProvider.GetUtcNow().UtcDateTime - message.CreatedAt).TotalMilliseconds);
        return activity;
    }

    private record ValidationResult(bool IsValid, string? Error)
    {
        public static ValidationResult Success() => new(true, null);
        public static ValidationResult Failure(string error) => new(false, error);
    }
}

