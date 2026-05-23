using Microsoft.EntityFrameworkCore;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.LoadTestSupport;
using CoreBankDemo.PaymentsAPI;

namespace CoreBankDemo.LoadTestSupport.Endpoints;

public static class ResetEndpoints
{
    private const decimal InitialBalance = LoadTestConstants.InitialBalance;

    public static void MapResetEndpoints(this IEndpointRouteBuilder app)
    {
        // Reset database to clean state for load testing
        app.MapPost("/reset", async (CoreBankDbContext coreBankDb, PaymentsDbContext paymentsDb, CancellationToken ct) =>
        {
            // Truncate all inbox/outbox tables in both databases
            await paymentsDb.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"OutboxMessages\" RESTART IDENTITY CASCADE", ct);
            await paymentsDb.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"InboxMessages\" RESTART IDENTITY CASCADE", ct);
            await coreBankDb.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"InboxMessages\" RESTART IDENTITY CASCADE", ct);
            await coreBankDb.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"MessagingOutboxMessages\" RESTART IDENTITY CASCADE", ct);

            // Reset all load test accounts to initial balance
            var accountCount = await coreBankDb.Database.ExecuteSqlRawAsync(
                "UPDATE \"Accounts\" SET \"Balance\" = {0}, \"UpdatedAt\" = NULL WHERE \"AccountNumber\" LIKE '%LOAD%'",
                InitialBalance);

            var totalBalance = accountCount * InitialBalance;

            return Results.Ok(new
            {
                Message = "Database reset complete",
                AccountsReset = accountCount,
                TotalBalance = totalBalance,
                InitialBalancePerAccount = InitialBalance
            });
        })
        .WithName("Reset")
        .WithSummary("Reset database to clean state for load testing");
    }
}
