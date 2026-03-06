using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.PaymentsAPI.Inbox;

public interface IInboxMessageRepository
{
    Task<bool> StoreIfNewAsync(
        PaymentsDbContext dbContext,
        InboxMessage message,
        CancellationToken cancellationToken);

    Task<InboxMessage?> LoadMessageAsync(
        PaymentsDbContext dbContext,
        Guid messageId,
        CancellationToken cancellationToken);

    Task<List<Guid>> GetPendingMessageIdsForPartitionAsync(
        PaymentsDbContext dbContext,
        int partitionId,
        CancellationToken cancellationToken);

    Task MarkAsProcessingAsync(
        PaymentsDbContext dbContext,
        InboxMessage message,
        CancellationToken cancellationToken);

    Task MarkAsCompletedAsync(
        PaymentsDbContext dbContext,
        InboxMessage message,
        CancellationToken cancellationToken);

    Task MarkMessageAsFailedWithRetryAsync(
        PaymentsDbContext dbContext,
        Guid messageId,
        string errorMessage,
        CancellationToken cancellationToken);
}

public class InboxMessageRepository(TimeProvider timeProvider) : IInboxMessageRepository
{
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);

    public async Task<bool> StoreIfNewAsync(
        PaymentsDbContext dbContext,
        InboxMessage message,
        CancellationToken cancellationToken)
    {
        var exists = await dbContext.InboxMessages
            .AnyAsync(m => m.IdempotencyKey == message.IdempotencyKey, cancellationToken);

        if (exists)
            return false;

        dbContext.InboxMessages.Add(message);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<InboxMessage?> LoadMessageAsync(
        PaymentsDbContext dbContext,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        return await dbContext.InboxMessages
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
    }

    public async Task<List<Guid>> GetPendingMessageIdsForPartitionAsync(
        PaymentsDbContext dbContext,
        int partitionId,
        CancellationToken cancellationToken)
    {
        var staleThreshold = timeProvider.GetUtcNow().Subtract(ProcessingTimeout).UtcDateTime;

        return await dbContext.InboxMessages
            .Where(m => m.PartitionId == partitionId &&
                       m.RetryCount < 5 &&
                       (m.Status == "Pending" ||
                        (m.Status == "Processing" && m.ReceivedAt < staleThreshold)))
            .OrderBy(m => m.ReceivedAt)
            .Take(10)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task MarkAsProcessingAsync(
        PaymentsDbContext dbContext,
        InboxMessage message,
        CancellationToken cancellationToken)
    {
        message.Status = "Processing";
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkAsCompletedAsync(
        PaymentsDbContext dbContext,
        InboxMessage message,
        CancellationToken cancellationToken)
    {
        message.Status = "Completed";
        message.ProcessedAt = timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkMessageAsFailedWithRetryAsync(
        PaymentsDbContext dbContext,
        Guid messageId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var message = await dbContext.InboxMessages
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);

        if (message != null)
        {
            message.Status = "Pending";
            message.RetryCount++;
            message.LastError = errorMessage;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}



