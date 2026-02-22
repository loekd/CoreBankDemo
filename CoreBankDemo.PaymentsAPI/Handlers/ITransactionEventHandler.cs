using CoreBankDemo.PaymentsAPI.Models;

namespace CoreBankDemo.PaymentsAPI.Handlers;

public interface ITransactionEventHandler
{
    Task HandleAsync(TransactionCompletedEvent transactionEvent, CancellationToken cancellationToken);
}

