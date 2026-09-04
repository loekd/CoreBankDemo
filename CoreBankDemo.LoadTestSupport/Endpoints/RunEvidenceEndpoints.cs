using CoreBankDemo.LoadTestSupport.Services;

namespace CoreBankDemo.LoadTestSupport.Endpoints;

public sealed record InlineSettlementEvidence(string IdempotencyKey);

public static class RunEvidenceEndpoints
{
    public static void MapRunEvidenceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/run-evidence/inline-settlement", (
            InlineSettlementEvidence? evidence,
            LoadRunEvidenceState state) =>
        {
            if (evidence is null)
            {
                return Results.BadRequest(new { Error = "A request body is required." });
            }

            if (string.IsNullOrWhiteSpace(evidence.IdempotencyKey)
                || !evidence.IdempotencyKey.StartsWith("load-test-", StringComparison.Ordinal))
            {
                return Results.BadRequest(new { Error = "A load-test idempotency key is required." });
            }

            state.RecordInlineSettlement(evidence.IdempotencyKey);
            return Results.Ok(new { state.InlineSettlementCount });
        })
        .WithName("RecordInlineSettlement")
        .WithSummary("Record one fresh instant payment that completed inline during the accepted k6 run");
    }
}
