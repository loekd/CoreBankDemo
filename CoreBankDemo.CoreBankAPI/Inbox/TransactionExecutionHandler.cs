using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI.Inbox;

internal sealed class TransactionExecutionHandler(
    ITransactionExecutor executor,
    IOutboxEventEnqueuer enqueuer,
    IInboxMessageRepository repository,
    CoreBankDbContext dbContext,
    TimeProvider timeProvider) : IInboxMessageHandler<InboxMessage>
{
    public async Task HandleAsync(InboxMessage message, CancellationToken cancellationToken = default)
    {
        var originalStatus = message.Status;
        var originalProcessedAt = message.ProcessedAt;
        var originalResponsePayload = message.ResponsePayload;

        try
        {
            await repository.ExecuteInTransactionAsync(async () =>
            {
                if (dbContext.Entry(message).State == EntityState.Detached)
                {
                    dbContext.Attach(message);
                }

                var result = await executor.ExecuteAsync(
                    message.FromAccount,
                    message.ToAccount,
                    message.Amount,
                    message.TransactionId,
                    cancellationToken);

                message.ResponsePayload = JsonSerializer.Serialize(result.Response);
                message.Status = MessageConstants.Status.Completed;
                message.ProcessedAt = timeProvider.GetUtcNow().UtcDateTime;

                if (result.Success)
                {
                    await enqueuer.EnqueueTransactionCompletedAsync(message, cancellationToken);
                    await enqueuer.EnqueueBalanceUpdatedAsync(
                        message,
                        message.FromAccount,
                        -message.Amount,
                        result.NewFromBalance!.Value,
                        cancellationToken);
                    await enqueuer.EnqueueBalanceUpdatedAsync(
                        message,
                        message.ToAccount,
                        message.Amount,
                        result.NewToBalance!.Value,
                        cancellationToken);
                }
                else
                {
                    await enqueuer.EnqueueTransactionFailedAsync(message, result.ErrorReason, cancellationToken);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }
        catch
        {
            message.Status = originalStatus;
            message.ProcessedAt = originalProcessedAt;
            message.ResponsePayload = originalResponsePayload;
            throw;
        }
    }
}
