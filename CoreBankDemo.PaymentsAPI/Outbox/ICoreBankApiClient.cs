using CoreBankDemo.PaymentsAPI.Models;

namespace CoreBankDemo.PaymentsAPI.Outbox;

public interface ICoreBankApiClient
{
    Task<AccountValidationResponse> ValidateAccountAsync(string accountNumber, CancellationToken cancellationToken);
    Task ProcessTransactionAsync(OutboxMessage message, CancellationToken cancellationToken);
}

