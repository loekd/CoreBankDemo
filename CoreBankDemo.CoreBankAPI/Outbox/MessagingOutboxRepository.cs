using CoreBankDemo.Messaging;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI.Outbox;

internal sealed class MessagingOutboxRepository(
    CoreBankDbContext dbContext,
    TimeProvider timeProvider)
    : OutboxMessageRepositoryBase<MessagingOutboxMessage, CoreBankDbContext>(dbContext, timeProvider)
{
    protected override DbSet<MessagingOutboxMessage> OutboxMessages => DbContext.MessagingOutboxMessages;
}
