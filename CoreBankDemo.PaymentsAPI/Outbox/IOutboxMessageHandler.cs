namespace CoreBankDemo.PaymentsAPI.Outbox;

public interface IOutboxMessageHandler
{
    Task<List<Guid>> GetPendingMessageIdsForPartitionAsync(PaymentsDbContext dbContext, int partitionId, CancellationToken cancellationToken);
    Task ProcessMessageAsync(Guid messageId, CancellationToken cancellationToken);
}

