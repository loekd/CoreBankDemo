using CoreBankDemo.LoadTestSupport;

namespace CoreBankDemo.LoadTestSupport.Endpoints;

public static class ResetEndpoints
{
    private const decimal InitialBalance = LoadTestConstants.InitialBalance;

    public static void MapResetEndpoints(this IEndpointRouteBuilder app)
    {
        // Reset database to clean state for load testing
        app.MapPost("/reset", async (DatabaseResetCoordinator coordinator, CancellationToken ct) =>
        {
            var result = await coordinator.ResetAndReleaseAsync(ct);

            return Results.Ok(new
            {
                Message = "Database reset complete",
                result.AccountsReset,
                result.TotalBalance,
                InitialBalancePerAccount = InitialBalance
            });
        })
        .WithName("Reset")
        .WithSummary("Reset database to clean state for load testing");
    }
}
