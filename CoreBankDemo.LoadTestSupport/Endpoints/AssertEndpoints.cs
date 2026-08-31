using CoreBankDemo.LoadTestSupport.Services;

namespace CoreBankDemo.LoadTestSupport.Endpoints;

public static class AssertEndpoints
{
    public static void MapAssertEndpoints(this IEndpointRouteBuilder app)
    {
        // Poll this until all four message stores (Payments outbox/inbox,
        // CoreBank inbox/outbox) are fully drained.
        app.MapGet("/assert/drain", async (LoadTestAssertionService assertionService, CancellationToken ct) =>
        {
            var result = await assertionService.CheckDrainAsync(ct);
            return Results.Ok(result);
        })
        .WithName("AssertDrain")
        .WithSummary("Poll until all four message stores are fully drained");

        // Full assertion suite — call this after drain reports IsDrained=true
        app.MapGet("/assert/results", async (
            int? expectedUnique,
            LoadTestAssertionService assertionService,
            CancellationToken ct) =>
        {
            var result = await assertionService.GetResultsAsync(expectedUnique, ct);
            return Results.Ok(result);
        })
        .WithName("AssertResults")
        .WithSummary("Full assertion suite: exactly-once, no duplicates, no failures, correct balances");
    }
}
