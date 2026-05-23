using CoreBankDemo.Messaging.Inbox;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.PaymentsAPI.Inbox;

public interface IInboxMessageRepository
{
    Task<bool> StoreIfNewAsync(
        InboxMessage message,
        CancellationToken cancellationToken);

    Task<InboxMessage?> LoadMessageAsync(
        Guid messageId,
        CancellationToken cancellationToken);

    Task<List<Guid>> GetPendingMessageIdsForPartitionAsync(
        int partitionId,
        CancellationToken cancellationToken);

    Task MarkAsProcessingAsync(
        InboxMessage message,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes <paramref name="work"/> and marks the message as completed atomically
    /// </summary>
    Task ExecuteInTransactionAsync(
        InboxMessage message,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken);

    Task MarkAsFailedWithRetryAsync(
        Guid messageId,
        string errorMessage,
        CancellationToken cancellationToken);
}

public class InboxMessageRepository : InboxMessageRepositoryBase<InboxMessage, PaymentsDbContext>, IInboxMessageRepository
{
    public InboxMessageRepository(PaymentsDbContext dbContext, TimeProvider timeProvider)
        : base(dbContext, timeProvider)
    {
    }

    protected override DbSet<InboxMessage> InboxMessages => DbContext.InboxMessages;
}
