using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI.Outbox;

internal sealed class MessagingOutboxRepository(
    CoreBankDbContext dbContext,
    TimeProvider timeProvider,
    BusinessMetrics businessMetrics)
    : OutboxMessageRepositoryBase<MessagingOutboxMessage, CoreBankDbContext>(dbContext, timeProvider, businessMetrics)
{
    protected override DbSet<MessagingOutboxMessage> OutboxMessages => DbContext.MessagingOutboxMessages;

    protected override BusinessMetrics.StoreName StoreName => BusinessMetrics.StoreName.CoreBankOutbox;
}
