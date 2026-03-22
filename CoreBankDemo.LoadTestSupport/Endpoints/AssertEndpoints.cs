using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.LoadTestSupport.Endpoints;

public static class AssertEndpoints
{
    public static void MapAssertEndpoints(this IEndpointRouteBuilder app)
    {
        // Poll this until all outbox messages are published AND all inbox messages are processed
        app.MapGet("/assert/drain", async (CoreBankReadDbContext coreBankDb, PaymentsReadDbContext paymentsDb, CancellationToken ct) =>
        {
            // Payments outbox: messages still waiting to be published via Dapr
            var outboxPending = await paymentsDb.OutboxMessages
                .CountAsync(m => m.Status == "Pending" || m.Status == "Processing", ct);

            // CoreBank inbox: messages received but not yet processed
            var inboxPending = await coreBankDb.InboxMessages
                .CountAsync(m => m.Status == "Pending" || m.Status == "Processing", ct);

            var inboxFailed = await coreBankDb.InboxMessages
                .CountAsync(m => m.Status == "Failed", ct);

            var inboxCompleted = await coreBankDb.InboxMessages
                .CountAsync(m => m.Status == "Completed", ct);

            return Results.Ok(new
            {
                IsDrained = outboxPending == 0 && inboxPending == 0,
                OutboxPending = outboxPending,
                InboxPending = inboxPending,
                Completed = inboxCompleted,
                Failed = inboxFailed
            });
        })
        .WithName("AssertDrain")
        .WithSummary("Poll until the inbox is fully drained");

        // Full assertion suite — call this after drain reports IsDrained=true
        app.MapGet("/assert/results", async (
            CoreBankReadDbContext coreBankDb,
            PaymentsReadDbContext paymentsDb,
            CancellationToken ct) =>
        {
            // Inbox stats from CoreBank
            var allInbox = await coreBankDb.InboxMessages.ToListAsync(ct);
            var completedCount = allInbox.Count(m => m.Status == "Completed");
            var failedCount = allInbox.Count(m => m.Status == "Failed");
            var pendingCount = allInbox.Count(m => m.Status == "Pending" || m.Status == "Processing");

            // Duplicate detection: same idempotency key appearing more than once
            var duplicateKeys = allInbox
                .GroupBy(m => m.IdempotencyKey)
                .Where(g => g.Count() > 1)
                .Select(g => new { Key = g.Key, Count = g.Count() })
                .ToList();

            // Outbox stats from Payments — each row is one unique payment submission
            var totalOutbox = await paymentsDb.OutboxMessages.CountAsync(ct);
            var outboxCompleted = await paymentsDb.OutboxMessages.CountAsync(m => m.Status == "Completed", ct);
            var outboxPending = await paymentsDb.OutboxMessages.CountAsync(m => m.Status == "Pending" || m.Status == "Processing", ct);

            // Account balances (for conservation check — caller compares with seed baseline)
            var accounts = await coreBankDb.Accounts.ToListAsync(ct);
            var totalBalance = accounts.Sum(a => a.Balance);

            var checks = new
            {
                NoFailedMessages = new
                {
                    Passed = failedCount == 0,
                    Detail = $"{failedCount} failed inbox message(s)"
                },
                NoPendingMessages = new
                {
                    Passed = pendingCount == 0,
                    Detail = $"{pendingCount} still pending/processing"
                },
                NoDuplicateProcessing = new
                {
                    Passed = duplicateKeys.Count == 0,
                    Detail = duplicateKeys.Count == 0
                        ? "No duplicates"
                        : $"{duplicateKeys.Count} duplicate key(s): {string.Join(", ", duplicateKeys.Select(d => $"{d.Key}(x{d.Count})"))}",
                    Duplicates = duplicateKeys
                },
                AllSubmittedProcessed = new
                {
                    Passed = completedCount == totalOutbox,
                    Detail = $"OutboxTotal={totalOutbox}, InboxCompleted={completedCount}"
                }
            };

            var allPassed =
                checks.NoFailedMessages.Passed &&
                checks.NoPendingMessages.Passed &&
                checks.NoDuplicateProcessing.Passed &&
                checks.AllSubmittedProcessed.Passed;

            return Results.Ok(new
            {
                AllPassed = allPassed,
                Checks = checks,
                Summary = new
                {
                    TotalOutbox = totalOutbox,
                    OutboxCompleted = outboxCompleted,
                    OutboxPending = outboxPending,
                    InboxCompleted = completedCount,
                    InboxFailed = failedCount,
                    InboxPending = pendingCount,
                    TotalBalance = totalBalance,
                    AccountCount = accounts.Count
                }
            });
        })
        .WithName("AssertResults")
        .WithSummary("Full assertion suite: exactly-once, no duplicates, no failures");
    }
}




