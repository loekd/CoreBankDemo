using System.Diagnostics;
using CoreBankDemo.PaymentsAPI.Models;

namespace CoreBankDemo.PaymentsAPI.Handlers;

public class TransactionEventHandler(ILogger<TransactionEventHandler> logger) : ITransactionEventHandler
{
    private static readonly ActivitySource ActivitySource = new(nameof(TransactionEventHandler));

    public Task HandleAsync(TransactionCompletedEvent transactionEvent, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("HandleTransactionCompletedEvent", ActivityKind.Consumer);
        activity?.SetTag("transaction.id", transactionEvent.TransactionId);
        activity?.SetTag("event.status", transactionEvent.Status);

        logger.LogInformation(
            "Received transaction completion for {TransactionId} with status {Status}",
            transactionEvent.TransactionId,
            transactionEvent.Status);

        return Task.CompletedTask;
    }

    public Task HandleAsync(BalanceUpdatedEvent balanceUpdatedEvent, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("HandleBalanceUpdatedEvent", ActivityKind.Consumer);
        activity?.SetTag("transaction.id", balanceUpdatedEvent.TransactionId);
        activity?.SetTag("account.number", balanceUpdatedEvent.AccountNumber);
        activity?.SetTag("account.new_balance", balanceUpdatedEvent.NewBalance);
        activity?.SetTag("account.delta", balanceUpdatedEvent.Delta);

        logger.LogInformation(
            "Received balance update for account {AccountNumber}: delta {Delta}, new balance {NewBalance} {Currency} (transaction {TransactionId})",
            balanceUpdatedEvent.AccountNumber,
            balanceUpdatedEvent.Delta,
            balanceUpdatedEvent.NewBalance,
            balanceUpdatedEvent.Currency,
            balanceUpdatedEvent.TransactionId);

        return Task.CompletedTask;
    }
}
