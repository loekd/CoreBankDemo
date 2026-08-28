using CoreBankDemo.ServiceDefaults.CloudEventTypes;

namespace CoreBankDemo.PaymentsAPI.Handlers;

public interface ITransactionEventHandler
{
    Task HandleAsync(TransactionCompletedEvent e, CancellationToken cancellationToken);
    Task HandleAsync(TransactionFailedEvent e, CancellationToken cancellationToken);
    Task HandleAsync(BalanceUpdatedEvent e, CancellationToken cancellationToken);
}
