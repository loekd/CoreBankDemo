using System.Diagnostics;
using System.Text.Json;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.PaymentsAPI.Handlers;

/// <summary>
/// Typed intake for the three known <c>transaction-events</c> CloudEvent
/// contracts (spec-5-5). Maps each event to the approved composite inbox
/// identity, serializes the typed payload, stamps injected time and ambient
/// trace context, and stores it via <see cref="IInboxMessageRepository"/>'s
/// insert-first dedupe (never check-then-insert -- AD-4). Public (unlike the
/// sibling <see cref="Outbox.IOutboxRepository"/>): <see
/// cref="Controllers.TransactionEventsController"/> is a public ASP.NET Core
/// controller (MVC only discovers public controller types), so its
/// constructor-injected dependency cannot be less accessible than the class
/// itself. Never processes event business behavior -- that is story 5.6's
/// job; this handler only accepts, dedupes, and durably stores.
/// </summary>
public interface ITransactionEventIntakeHandler
{
    Task StoreAsync(TransactionCompletedEvent transactionCompleted, CancellationToken cancellationToken);

    Task StoreAsync(TransactionFailedEvent transactionFailed, CancellationToken cancellationToken);

    Task StoreAsync(BalanceUpdatedEvent balanceUpdated, CancellationToken cancellationToken);
}

internal sealed class TransactionEventIntakeHandler(
    IInboxMessageRepository repository,
    IOptions<InboxProcessingOptions> inboxOptions,
    TimeProvider timeProvider,
    ILogger<TransactionEventIntakeHandler> logger) : ITransactionEventIntakeHandler
{
    /// <summary>
    /// Transaction-wide events store under the empty-account sentinel
    /// (spec-5-5's I/O matrix): a transaction can complete/fail once, but its
    /// balance-update events are per-account, so only those carry a real
    /// <see cref="InboxMessage.AccountNumber"/>.
    /// </summary>
    private const string TransactionWideAccountSentinel = "";

    public Task StoreAsync(TransactionCompletedEvent transactionCompleted, CancellationToken cancellationToken) =>
        StoreAsync(
            transactionCompleted.TransactionId,
            Constants.TransactionCompleted,
            TransactionWideAccountSentinel,
            transactionCompleted,
            cancellationToken);

    public Task StoreAsync(TransactionFailedEvent transactionFailed, CancellationToken cancellationToken) =>
        StoreAsync(
            transactionFailed.TransactionId,
            Constants.TransactionFailed,
            TransactionWideAccountSentinel,
            transactionFailed,
            cancellationToken);

    public Task StoreAsync(BalanceUpdatedEvent balanceUpdated, CancellationToken cancellationToken) =>
        StoreAsync(
            balanceUpdated.TransactionId,
            Constants.BalanceUpdated,
            balanceUpdated.AccountNumber,
            balanceUpdated,
            cancellationToken);

    private async Task StoreAsync<TEvent>(
        string transactionId,
        string eventType,
        string accountNumber,
        TEvent payload,
        CancellationToken cancellationToken)
    {
        // AD-4: IdempotencyKey stays the bounded transaction identifier (never
        // a concatenated legacy key, which could exceed the schema's
        // 100-character limit) -- the composite unique index
        // (TransactionId, EventType, AccountNumber) alone owns event dedupe,
        // and every event for one transaction lands on the same partition.
        var partitionId = PartitionHelper.GetPartitionId(transactionId, inboxOptions.Value.PartitionCount);

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["TransactionId"] = transactionId,
            ["EventType"] = eventType,
            ["AccountNumber"] = accountNumber,
            ["PartitionId"] = partitionId
        });

        var message = new InboxMessage
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = transactionId,
            TransactionId = transactionId,
            EventType = eventType,
            AccountNumber = accountNumber,
            Payload = JsonSerializer.Serialize(payload),
            PartitionId = partitionId,
            Status = MessageConstants.Status.Pending,
            ReceivedAt = timeProvider.GetUtcNow().UtcDateTime,
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString
        };

        var stored = await repository.StoreIfNewAsync(message, cancellationToken).ConfigureAwait(false);
        if (stored)
        {
            logger.LogInformation(
                "Stored transaction event {EventType} for transaction {TransactionId}, account {AccountNumber}, partition {PartitionId}",
                eventType, transactionId, accountNumber, partitionId);
        }
        else
        {
            logger.LogInformation(
                "Duplicate transaction event ignored: {EventType} for transaction {TransactionId}, account {AccountNumber}",
                eventType, transactionId, accountNumber);
        }
    }
}
