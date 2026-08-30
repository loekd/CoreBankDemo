using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.PaymentsAPI.Outbox;

internal interface IOutboxRepository
{
    Task<bool> StoreIfNewAsync(OutboxMessage message, CancellationToken cancellationToken);

    Task<OutboxMessage?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken);
}

internal sealed class OutboxRepository(PaymentsDbContext dbContext, TimeProvider timeProvider, BusinessMetrics businessMetrics)
    : OutboxMessageRepositoryBase<OutboxMessage, PaymentsDbContext>(dbContext, timeProvider, businessMetrics), IOutboxRepository
{
    protected override DbSet<OutboxMessage> OutboxMessages => DbContext.OutboxMessages;

    protected override BusinessMetrics.StoreName StoreName => BusinessMetrics.StoreName.PaymentsOutbox;

    public Task<OutboxMessage?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        OutboxMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(message => message.IdempotencyKey == idempotencyKey, cancellationToken);
}
