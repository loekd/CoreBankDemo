using CoreBankDemo.PaymentsAPI.Models;
using Dapr.Client;

namespace CoreBankDemo.PaymentsAPI.Outbox;

public class DaprCoreBankApiClient(DaprClient daprClient) : ICoreBankApiClient
{
    private const string AppId = "corebank-api";

    public async Task<AccountValidationResponse> ValidateAccountAsync(string accountNumber, CancellationToken cancellationToken)
    {
        return await daprClient.InvokeMethodAsync<object, AccountValidationResponse>(
            AppId,
            "api/accounts/validate",
            new { AccountNumber = accountNumber },
            cancellationToken);
    }

    public async Task ProcessTransactionAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        await daprClient.InvokeMethodAsync(
            AppId,
            "api/transactions/process",
            new
            {
                message.FromAccount,
                message.ToAccount,
                message.Amount,
                message.Currency,
                message.TransactionId
            },
            cancellationToken);
    }
}

