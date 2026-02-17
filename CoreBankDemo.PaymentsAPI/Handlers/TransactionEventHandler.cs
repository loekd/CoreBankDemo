using CoreBankDemo.PaymentsAPI.Models;

namespace CoreBankDemo.PaymentsAPI.Handlers;

public class TransactionEventHandler(ILogger<TransactionEventHandler> logger) : ITransactionEventHandler
{
    public Task<TransactionResponse> HandleAsync(TransactionCompletedEvent transactionEvent, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Received transaction completion for {TransactionId} with status {Status}",
            transactionEvent.TransactionId,
            transactionEvent.Status);

        var response = new TransactionResponse(
            transactionEvent.TransactionId,
            transactionEvent.Status,
            transactionEvent.ProcessedAt);

        return Task.FromResult(response);
    }
}

