using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreBankDemo.PaymentsAPI.Outbox;

public interface IOutboxRepository
{
    Task<OutboxMessage?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<bool> StoreIfNewAsync(
        OutboxMessage message,
        CancellationToken cancellationToken);
}

public class OutboxRepository(PaymentsDbContext dbContext) : IOutboxRepository
{
    public async Task<OutboxMessage?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        return await dbContext.OutboxMessages
            .FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey, cancellationToken);
    }

    public async Task<bool> StoreIfNewAsync(
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        var exists = await dbContext.OutboxMessages
            .AnyAsync(m => m.IdempotencyKey == message.IdempotencyKey, cancellationToken);

        if (exists)
            return false;

        dbContext.OutboxMessages.Add(message);
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
}


