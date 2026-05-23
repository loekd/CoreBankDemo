using System.Diagnostics;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging.Inbox;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.CoreBankAPI.Inbox;

public class InboxProcessor : InboxProcessorBase<InboxMessage, CoreBankDbContext>
{
    private readonly TransactionValidator _transactionValidator;

    public InboxProcessor(
        IServiceProvider serviceProvider,
        ILogger<InboxProcessor> logger,
        IDistributedLockService lockService,
        TransactionValidator transactionValidator,
        IOptions<InboxProcessingOptions> options,
        TimeProvider timeProvider)
        : base(serviceProvider, logger, lockService, options, timeProvider, "InboxProcessor")
    {
        _transactionValidator = transactionValidator;
    }

    protected override string LockNamePrefix => "inbox";

    protected override async Task ProcessMessageAsync(
        InboxMessage message,
        IServiceProvider scopedServiceProvider,
        CancellationToken cancellationToken)
    {
        var dbContext = GetService<CoreBankDbContext>(scopedServiceProvider);
        var repository = GetService<IInboxMessageRepository>(scopedServiceProvider);
        var executor = GetService<ITransactionExecutor>(scopedServiceProvider);
        var publisher = GetService<IOutboxPublisher>(scopedServiceProvider);
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                var (fromAccount, toAccount) = await executor.LoadAccountsAsync(dbContext, message, cancellationToken);
                var validationResult = _transactionValidator.ValidateTransaction(message, fromAccount, toAccount);

                if (!validationResult.IsValid)
                {
                    await HandleFailedTransactionAsync(dbContext, message, validationResult, executor, publisher, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return;
                }

                await ExecuteSuccessfulTransactionAsync(dbContext, message, fromAccount!, toAccount!, executor, publisher, cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                LogTransactionSuccess(message, scopedServiceProvider);
            }
            catch (Exception ex)
            {
                await HandleTransactionErrorAsync(transaction, repository, message.Id, ex, cancellationToken);
            }
        });
    }

    private async Task HandleFailedTransactionAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        ValidationResult validationResult,
        ITransactionExecutor executor,
        IOutboxPublisher publisher,
        CancellationToken cancellationToken)
    {
        executor.PrepareFailedTransaction(message, validationResult.Error);
        await publisher.PublishTransactionFailedAsync(dbContext, message, validationResult.Error, cancellationToken);
        await executor.SaveAsync(dbContext, cancellationToken);
    }

    private async Task ExecuteSuccessfulTransactionAsync(
        CoreBankDbContext dbContext,
        InboxMessage message,
        Account fromAccount,
        Account toAccount,
        ITransactionExecutor executor,
        IOutboxPublisher publisher,
        CancellationToken cancellationToken)
    {
        var (newFromBalance, newToBalance) = executor.ApplySuccessfulTransaction(message, fromAccount, toAccount);

        await publisher.PublishTransactionCompletedAsync(dbContext, message, cancellationToken);
        await publisher.PublishBalanceUpdatedAsync(dbContext, message, message.FromAccount, newFromBalance, cancellationToken);
        await publisher.PublishBalanceUpdatedAsync(dbContext, message, message.ToAccount, newToBalance, cancellationToken);

        await executor.SaveAsync(dbContext, cancellationToken);
    }

    private async Task HandleTransactionErrorAsync(
        IDbContextTransaction transaction,
        IInboxMessageRepository repository,
        Guid messageId,
        Exception ex,
        CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        await repository.MarkAsFailedWithRetryAsync(messageId, ex.Message, cancellationToken);
    }

    private void LogTransactionSuccess(InboxMessage message, IServiceProvider scopedServiceProvider)
    {
        var logger = scopedServiceProvider.GetRequiredService<ILogger<InboxProcessor>>();
        logger.LogInformation(
            "Successfully processed inbox message {MessageId} with idempotency key {IdempotencyKey}, transaction {TransactionId}. " +
            "Transferred {Amount} {Currency} from {FromAccount} to {ToAccount}",
            message.Id, message.IdempotencyKey, message.TransactionId, message.Amount, message.Currency,
            message.FromAccount, message.ToAccount);
    }
}
