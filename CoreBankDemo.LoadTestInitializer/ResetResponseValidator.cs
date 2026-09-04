using System.Text.Json;

namespace CoreBankDemo.LoadTestInitializer;

public static class ResetResponseValidator
{
    // Intentionally duplicated from CoreBankDemo.LoadTestSupport.LoadTestConstants:
    // this project stays a lightweight console app and does not reference
    // LoadTestSupport (which would pull in EF Core, MCP, and both API projects).
    // Keep these two literals in sync with LoadTestConstants.AccountCount/InitialBalance.
    internal const int ExpectedAccountCount = 10;
    internal const decimal InitialBalance = 10_000_000m;
    internal const decimal ExpectedTotalBalance = ExpectedAccountCount * InitialBalance;

    public static void Validate(string responseBody)
    {
        ResetResponse response;
        try
        {
            response = JsonSerializer.Deserialize<ResetResponse>(responseBody, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Reset response was JSON null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Reset response was not valid JSON.", exception);
        }

        if (response.Message != "Database reset complete"
            || response.AccountsReset != ExpectedAccountCount
            || response.InitialBalancePerAccount != InitialBalance
            || response.TotalBalance != ExpectedTotalBalance)
        {
            throw new InvalidDataException(
                $"Reset response was semantically incomplete: message='{response.Message}', accountsReset={response.AccountsReset}, totalBalance={response.TotalBalance}, initialBalancePerAccount={response.InitialBalancePerAccount}.");
        }
    }

    private sealed record ResetResponse(
        string? Message,
        int AccountsReset,
        decimal TotalBalance,
        decimal InitialBalancePerAccount);
}
