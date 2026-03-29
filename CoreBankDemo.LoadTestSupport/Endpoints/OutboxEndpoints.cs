using Microsoft.EntityFrameworkCore;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.PaymentsAPI;

namespace CoreBankDemo.LoadTestSupport.Endpoints;

public static class OutboxEndpoints
{
    public static void MapOutboxEndpoints(this IEndpointRouteBuilder app)
    {
        // CoreBank Outbox Endpoints
        app.MapGet("/corebank/outbox", async (CoreBankDbContext db, CancellationToken ct) =>
        {
            var messages = await db.MessagingOutboxMessages
                .OrderByDescending(m => m.CreatedAt)
                .Take(50)
                .ToListAsync(ct);

            return Results.Ok(messages);
        })
        .WithName("GetCoreBankOutbox")
        .WithSummary("Get recent CoreBank outbox messages");

        // Payments Outbox Endpoints
        app.MapGet("/payments/outbox", async (PaymentsDbContext db, CancellationToken ct) =>
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
