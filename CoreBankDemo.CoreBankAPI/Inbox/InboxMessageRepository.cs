using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Diagnostics;

namespace CoreBankDemo.CoreBankAPI.Inbox;

public interface IInboxMessageRepository
{
    Task<InboxMessage?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<bool> StoreIfNewAsync(
        InboxMessage message,
        CancellationToken cancellationToken);

    Task<InboxMessage?> LoadMessageAsync(
        CoreBankDbContext dbContext,
        Guid messageId,
        CancellationToken cancellationToken);

    Task<List<Guid>> GetPendingMessageIdsForPartitionAsync(
        CoreBankDbContext dbContext,
        int partitionId,
        CancellationToken cancellationToken);

    Task MarkMessageAsFailedWithRetryAsync(
        CoreBankDbContext dbContext,
        Guid messageId,
        string errorMessage,
        CancellationToken cancellationToken);
}

public class InboxMessageRepository(IServiceProvider serviceProvider, TimeProvider timeProvider) : IInboxMessageRepository
{
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);

    public async Task<InboxMessage?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();

        return await dbContext.InboxMessages
            .FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey, cancellationToken);
    }

    public async Task<bool> StoreIfNewAsync(
        InboxMessage message,
        CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();

        var exists = await dbContext.InboxMessages
            .AnyAsync(m => m.IdempotencyKey == message.IdempotencyKey, cancellationToken);

        if (exists)
            return false;

        dbContext.InboxMessages.Add(message);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Concurrent insert race — already stored by another instance
            return false;
        }
    }

    public async Task<InboxMessage?> LoadMessageAsync(
        CoreBankDbContext dbContext,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        return await dbContext.InboxMessages
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
    }

    public async Task<List<Guid>> GetPendingMessageIdsForPartitionAsync(
        CoreBankDbContext dbContext,
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

    public async Task MarkMessageAsFailedWithRetryAsync(
        CoreBankDbContext dbContext,
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
