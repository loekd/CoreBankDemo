using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.LoadTestSupport.Endpoints;

public static class OutboxEndpoints
{
    public static void MapOutboxEndpoints(this IEndpointRouteBuilder app)
    {
        // CoreBank Outbox Endpoints
        app.MapGet("/corebank/outbox", async (CoreBankReadDbContext db, CancellationToken ct) =>
        {
            var messages = await db.OutboxMessages
                .OrderByDescending(m => m.CreatedAt)
                .Take(50)
                .ToListAsync(ct);

            return Results.Ok(messages);
        })
        .WithName("GetCoreBankOutbox")
        .WithSummary("Get recent CoreBank outbox messages");

        // Payments Outbox Endpoints
        app.MapGet("/payments/outbox", async (PaymentsReadDbContext db, CancellationToken ct) =>
        {
            var messages = await db.OutboxMessages
                .OrderByDescending(m => m.CreatedAt)
                .Take(50)
                .ToListAsync(ct);

            return Results.Ok(messages);
        })
        .WithName("GetPaymentsOutbox")
        .WithSummary("Get recent Payments outbox messages");
    }
}
