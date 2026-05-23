using CoreBankDemo.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;

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

public class OutboxRepository(PaymentsDbContext dbContext, TimeProvider timeProvider)
    : OutboxMessageRepositoryBase<OutboxMessage, PaymentsDbContext>(dbContext, timeProvider), IOutboxRepository
{
    protected override DbSet<OutboxMessage> OutboxMessages => DbContext.OutboxMessages;
}
