using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.LoadTestSupport.Endpoints;

public static class InboxEndpoints
{
    public static void MapInboxEndpoints(this IEndpointRouteBuilder app)
    {
        // CoreBank Inbox Endpoints
        app.MapGet("/corebank/inbox", async (CoreBankReadDbContext db, CancellationToken ct) =>
        {
            var messages = await db.InboxMessages
                .OrderByDescending(m => m.ReceivedAt)
                .Take(50)
                .ToListAsync(ct);

            return Results.Ok(messages);
        })
        .WithName("GetCoreBankInbox")
        .WithSummary("Get recent CoreBank inbox messages");

        // Payments Inbox Endpoints
        app.MapGet("/payments/inbox", async (PaymentsReadDbContext db, CancellationToken ct) =>
        {
            var messages = await db.InboxMessages
                .OrderByDescending(m => m.ReceivedAt)
                .Take(50)
                .ToListAsync(ct);

            return Results.Ok(messages);
        })
        .WithName("GetPaymentsInbox")
        .WithSummary("Get recent Payments inbox messages");
    }
}
