using CoreBankDemo.PaymentsAPI.Models;

namespace CoreBankDemo.PaymentsAPI.Handlers;

public interface ITransactionEventHandler
{
    Task<TransactionResponse> HandleAsync(TransactionCompletedEvent transactionEvent, CancellationToken cancellationToken);
}

