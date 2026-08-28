using System.Diagnostics;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;

namespace CoreBankDemo.PaymentsAPI.Handlers;

public class TransactionEventHandler(ILogger<TransactionEventHandler> logger) : ITransactionEventHandler
{
    private static readonly ActivitySource ActivitySource = new(nameof(TransactionEventHandler));

    public Task HandleAsync(TransactionCompletedEvent e, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("HandleTransactionCompletedEvent", ActivityKind.Consumer);
        activity?.SetTag("transaction.id", e.TransactionId);
        activity?.SetTag("event.status", e.Status);

        logger.LogInformation(
            "Transaction {TransactionId} completed with status {Status}",
            e.TransactionId, e.Status);

        return Task.CompletedTask;
    }

    public Task HandleAsync(TransactionFailedEvent e, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("HandleTransactionFailedEvent", ActivityKind.Consumer);
        activity?.SetTag("transaction.id", e.TransactionId);
        activity?.SetTag("event.status", e.Status);
        activity?.SetTag("error.reason", e.ErrorReason);

        logger.LogWarning(
            "Transaction {TransactionId} failed: {ErrorReason}",
            e.TransactionId, e.ErrorReason);

        return Task.CompletedTask;
    }

    public Task HandleAsync(BalanceUpdatedEvent e, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("HandleBalanceUpdatedEvent", ActivityKind.Consumer);
        activity?.SetTag("transaction.id", e.TransactionId);
        activity?.SetTag("account.number", e.AccountNumber);
        activity?.SetTag("account.new_balance", e.NewBalance);
        activity?.SetTag("account.delta", e.Delta);

        logger.LogInformation(
            "Balance updated for account {AccountNumber}: delta {Delta}, new balance {NewBalance} {Currency} (transaction {TransactionId})",
            e.AccountNumber, e.Delta, e.NewBalance, e.Currency, e.TransactionId);

        return Task.CompletedTask;
    }
}
