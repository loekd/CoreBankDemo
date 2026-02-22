using System.Diagnostics;
using CoreBankDemo.PaymentsAPI.Models;

namespace CoreBankDemo.PaymentsAPI.Handlers;

public class TransactionEventHandler(ILogger<TransactionEventHandler> logger) : ITransactionEventHandler
{
    private static readonly ActivitySource ActivitySource = new(nameof(TransactionEventHandler));

    public Task HandleAsync(TransactionCompletedEvent transactionEvent, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("HandleTransactionEvent", ActivityKind.Consumer);
        activity?.SetTag("transaction.id", transactionEvent.TransactionId);
        activity?.SetTag("event.status", transactionEvent.Status);

        logger.LogInformation(
            "Received transaction completion for {TransactionId} with status {Status}",
            transactionEvent.TransactionId,
            transactionEvent.Status);

        return Task.CompletedTask;
    }
}
