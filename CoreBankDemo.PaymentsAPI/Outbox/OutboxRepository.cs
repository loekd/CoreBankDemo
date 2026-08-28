using CoreBankDemo.Messaging;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.PaymentsAPI.Outbox;

internal interface IOutboxRepository
{
    Task<bool> StoreIfNewAsync(OutboxMessage message, CancellationToken cancellationToken);

    Task<OutboxMessage?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken);
}

internal sealed class OutboxRepository(PaymentsDbContext dbContext, TimeProvider timeProvider)
    : OutboxMessageRepositoryBase<OutboxMessage, PaymentsDbContext>(dbContext, timeProvider), IOutboxRepository
{
    protected override DbSet<OutboxMessage> OutboxMessages => DbContext.OutboxMessages;

    public Task<OutboxMessage?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        OutboxMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(message => message.IdempotencyKey == idempotencyKey, cancellationToken);
}
