using System.Diagnostics;
using CoreBankDemo.PaymentsAPI.Models;

namespace CoreBankDemo.PaymentsAPI.Outbox;

public class HttpCoreBankApiClient(HttpClient httpClient) : ICoreBankApiClient
{
    public async Task<AccountValidationResponse> ValidateAccountAsync(string accountNumber, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/accounts/validate");
        request.Content = JsonContent.Create(new { AccountNumber = accountNumber });
        PropagateTraceContext(request);

        var response = await httpClient.SendAsync(request, cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<AccountValidationResponse>(cancellationToken)
               ?? throw new InvalidOperationException("Empty response from Core Bank API account validation.");
    }

    public async Task ProcessTransactionAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/transactions/process");
        request.Content = JsonContent.Create(new
        {
            message.FromAccount,
            message.ToAccount,
            message.Amount,
            message.Currency,
            message.TransactionId
        });
        PropagateTraceContext(request);

        var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static void PropagateTraceContext(HttpRequestMessage request)
    {
        var activity = Activity.Current;
        if (activity is null)
            return;

        request.Headers.TryAddWithoutValidation("traceparent", activity.Id);

        if (!string.IsNullOrEmpty(activity.TraceStateString))
            request.Headers.TryAddWithoutValidation("tracestate", activity.TraceStateString);
    }
}



