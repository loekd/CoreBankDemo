using CoreBankDemo.Messaging.Inbox;
using Microsoft.EntityFrameworkCore;

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
        Guid messageId,
        CancellationToken cancellationToken);

    Task<List<Guid>> GetPendingMessageIdsForPartitionAsync(
        int partitionId,
        CancellationToken cancellationToken);

    Task MarkMessageAsFailedWithRetryAsync(
        Guid messageId,
        string errorMessage,
        CancellationToken cancellationToken);
}

public class InboxMessageRepository : InboxMessageRepositoryBase<InboxMessage, CoreBankDbContext>, IInboxMessageRepository
{
    public InboxMessageRepository(CoreBankDbContext dbContext, TimeProvider timeProvider)
        : base(dbContext, timeProvider)
    {
    }

    protected override DbSet<InboxMessage> InboxMessages => DbContext.InboxMessages;
}
