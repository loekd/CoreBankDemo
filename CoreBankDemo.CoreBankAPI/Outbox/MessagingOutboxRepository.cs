using CoreBankDemo.Messaging;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI.Outbox;

internal sealed class MessagingOutboxRepository
    : OutboxMessageRepositoryBase<MessagingOutboxMessage, CoreBankDbContext>
{
    public MessagingOutboxRepository(CoreBankDbContext dbContext, TimeProvider timeProvider)
        : base(dbContext, timeProvider)
    {
    }

    protected override DbSet<MessagingOutboxMessage> OutboxMessages => DbContext.MessagingOutboxMessages;
}
