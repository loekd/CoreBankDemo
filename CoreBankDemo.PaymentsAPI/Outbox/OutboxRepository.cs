using CoreBankDemo.Messaging.Outbox;
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

public class OutboxRepository : OutboxMessageRepositoryBase<OutboxMessage, PaymentsDbContext>, IOutboxRepository
{
    public OutboxRepository(PaymentsDbContext dbContext, TimeProvider timeProvider)
        : base(dbContext, timeProvider)
    {
    }

    protected override DbSet<OutboxMessage> OutboxMessages => DbContext.OutboxMessages;

    public async Task<OutboxMessage?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        return await OutboxMessages
            .FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey, cancellationToken);
    }

    public async Task<bool> StoreIfNewAsync(
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        var exists = await OutboxMessages
            .AnyAsync(m => m.IdempotencyKey == message.IdempotencyKey, cancellationToken);

        if (exists)
            return false;

        OutboxMessages.Add(message);
        try
        {
            await DbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Concurrent insert race — already stored by another instance
            return false;
        }
    }
}


