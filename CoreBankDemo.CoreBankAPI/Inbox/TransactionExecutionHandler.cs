using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI.Inbox;

internal sealed class TransactionExecutionHandler(
    ITransactionExecutor executor,
    IOutboxEventEnqueuer enqueuer,
    IInboxMessageRepository repository,
    CoreBankDbContext dbContext,
    TimeProvider timeProvider,
    BusinessMetrics businessMetrics) : IInboxMessageHandler<InboxMessage>
{
    public async Task HandleAsync(InboxMessage message, CancellationToken cancellationToken = default)
    {
        var originalStatus = message.Status;
        var originalProcessedAt = message.ProcessedAt;
        var originalResponsePayload = message.ResponsePayload;
        // Captured inside the transaction delegate below and read only after
        // ExecuteInTransactionAsync returns normally (i.e. after commit) —
        // reset on every retry attempt the execution strategy makes, so a
        // transient-failure retry can never leave a stale value from an
        // attempt whose transaction actually rolled back.
        bool? executionSucceeded = null;
        var enqueuedEventCount = 0;

        try
        {
            await repository.ExecuteInTransactionAsync(async () =>
            {
                executionSucceeded = null;
                enqueuedEventCount = 0;

                if (dbContext.Entry(message).State == EntityState.Detached)
                {
                    dbContext.Attach(message);
                }

                var result = await ExecuteTimedAsync(message, cancellationToken);

                message.ResponsePayload = JsonSerializer.Serialize(result.Response);
                message.Status = MessageConstants.Status.Completed;
                message.ProcessedAt = timeProvider.GetUtcNow().UtcDateTime;

                if (result.Success)
                {
                    await enqueuer.EnqueueTransactionCompletedAsync(message, cancellationToken);
                    enqueuedEventCount++;
                    await enqueuer.EnqueueBalanceUpdatedAsync(
                        message,
                        message.FromAccount,
                        -message.Amount,
                        result.NewFromBalance!.Value,
                        cancellationToken);
                    enqueuedEventCount++;
                    await enqueuer.EnqueueBalanceUpdatedAsync(
                        message,
                        message.ToAccount,
                        message.Amount,
                        result.NewToBalance!.Value,
                        cancellationToken);
                    enqueuedEventCount++;
                }
                else
                {
                    await enqueuer.EnqueueTransactionFailedAsync(message, result.ErrorReason, cancellationToken);
                    enqueuedEventCount++;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                executionSucceeded = result.Success;
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            message.Status = originalStatus;
            message.ProcessedAt = originalProcessedAt;
            message.ResponsePayload = originalResponsePayload;
            throw;
        }
        catch
        {
            message.Status = originalStatus;
            message.ProcessedAt = originalProcessedAt;
            message.ResponsePayload = originalResponsePayload;
            for (var i = 0; i < enqueuedEventCount; i++)
            {
                businessMetrics.RecordStoreOperation(
                    BusinessMetrics.StoreName.CoreBankOutbox,
                    BusinessMetrics.StoreKind.Outbox,
                    BusinessMetrics.StoreOperationOutcome.Failed);
            }

            throw;
        }

        // Story 6.5: recorded only here, after ExecuteInTransactionAsync
        // returned normally (i.e. its transaction committed) — a rollback
        // rethrows above and is caught by the try/catch, so this line is
        // never reached for a rolled-back attempt. IOutboxEventEnqueuer adds
        // its rows directly to the DbContext (never through
        // MessageRepositoryBase.StoreIfNewAsync's own store-operation
        // recording), so this is the only place these corebank-outbox
        // `added` operations are ever counted.
        //
        // The same holds for the corebank-inbox completion: this handler
        // commits the row as Completed itself, on both the inline intake path
        // and the background inbox processor path, so InboxProcessorBase's own
        // MarkAsCompletedAsync always sees AlreadyTerminal and records nothing.
        // Business success and business rejection both complete the row.
        if (executionSucceeded is not null)
        {
            businessMetrics.RecordItemProcessed(
                BusinessMetrics.StoreName.CoreBankInbox,
                BusinessMetrics.StoreKind.Inbox,
                BusinessMetrics.ItemOutcome.Completed);
        }

        if (executionSucceeded is true)
        {
            businessMetrics.RecordTransactionProcessed(BusinessMetrics.TransactionProcessedOutcome.Completed);
            businessMetrics.RecordStoreOperation(
                BusinessMetrics.StoreName.CoreBankOutbox, BusinessMetrics.StoreKind.Outbox, BusinessMetrics.StoreOperationOutcome.Added);
            businessMetrics.RecordStoreOperation(
                BusinessMetrics.StoreName.CoreBankOutbox, BusinessMetrics.StoreKind.Outbox, BusinessMetrics.StoreOperationOutcome.Added);
            businessMetrics.RecordStoreOperation(
                BusinessMetrics.StoreName.CoreBankOutbox, BusinessMetrics.StoreKind.Outbox, BusinessMetrics.StoreOperationOutcome.Added);
        }
        else if (executionSucceeded is false)
        {
            businessMetrics.RecordTransactionProcessed(BusinessMetrics.TransactionProcessedOutcome.BusinessRejected);
            businessMetrics.RecordStoreOperation(
                BusinessMetrics.StoreName.CoreBankOutbox, BusinessMetrics.StoreKind.Outbox, BusinessMetrics.StoreOperationOutcome.Added);
        }
    }

    /// <summary>
    /// Runs the executor and records <c>corebankdemo.transaction.execution.duration</c>
    /// for the attempt. Queue duration only measures how long the row waited
    /// before pickup; this is the only measurement of the execution itself, so
    /// it is recorded the moment the executor returns or throws (per attempt,
    /// before commit) rather than with the committed-outcome counters below.
    /// A caller-requested cancellation is not an execution outcome and records
    /// nothing, matching the handler's own cancellation path.
    /// </summary>
    private async Task<TransactionExecutionResult> ExecuteTimedAsync(InboxMessage message, CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        TransactionExecutionResult result;
        try
        {
            result = await executor.ExecuteAsync(
                message.FromAccount,
                message.ToAccount,
                message.Amount,
                message.TransactionId,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            businessMetrics.RecordTransactionExecutionDuration(
                BusinessMetrics.TransactionExecutionOutcome.Failed, timeProvider.GetUtcNow() - startedAt);
            throw;
        }

        businessMetrics.RecordTransactionExecutionDuration(
            result.Success
                ? BusinessMetrics.TransactionExecutionOutcome.Completed
                : BusinessMetrics.TransactionExecutionOutcome.BusinessRejected,
            timeProvider.GetUtcNow() - startedAt);
        return result;
    }
}
