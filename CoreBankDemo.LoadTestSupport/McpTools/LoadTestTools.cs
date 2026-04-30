using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.PaymentsAPI;
using static CoreBankDemo.Messaging.MessageConstants;

namespace CoreBankDemo.LoadTestSupport.McpTools;

[McpServerToolType]
public sealed class LoadTestTools
{
    private const decimal InitialBalance = LoadTestConstants.InitialBalance;
    private const int LoadTestAccountCount = LoadTestConstants.AccountCount;

    [McpServerTool(Name = "reset_database")]
    [Description(
        "⚠️ DESTRUCTIVE: Truncates ALL inbox/outbox tables in both databases and resets all " +
        "load test account balances to 10,000,000 EUR. Call this BEFORE starting a load test " +
        "to ensure a clean baseline. This cannot be undone.")]
    public static async Task<string> ResetDatabase(
        CoreBankDbContext coreBankDb,
        PaymentsDbContext paymentsDb,
        CancellationToken ct)
    {
        try
        {
            await paymentsDb.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE \"OutboxMessages\" RESTART IDENTITY CASCADE", ct);
            await paymentsDb.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE \"InboxMessages\" RESTART IDENTITY CASCADE", ct);
            await coreBankDb.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE \"InboxMessages\" RESTART IDENTITY CASCADE", ct);
            await coreBankDb.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE \"MessagingOutboxMessages\" RESTART IDENTITY CASCADE", ct);

            var accountCount = await coreBankDb.Database.ExecuteSqlRawAsync(
                "UPDATE \"Accounts\" SET \"Balance\" = {0}, \"UpdatedAt\" = NULL WHERE \"AccountNumber\" LIKE '%LOAD%'",
                InitialBalance);

            return JsonSerializer.Serialize(new
            {
                success = true,
                accountsReset = accountCount,
                initialBalancePerAccount = InitialBalance,
                totalBalance = accountCount * InitialBalance
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = "reset_failed", detail = ex.Message });
        }
    }

    [McpServerTool(Name = "poll_until_drained")]
    [Description(
        "Polls the inbox/outbox until all messages are fully processed (drained) or the timeout " +
        "is reached. Call this AFTER the load test run completes. The tool handles internal " +
        "polling every 2 seconds — do NOT call repeatedly. Returns the final drain status.")]
    public static async Task<string> PollUntilDrained(
        CoreBankDbContext coreBankDb,
        PaymentsDbContext paymentsDb,
        [Description("Maximum seconds to wait for drain (default 60, max 300)")]
        int timeoutSeconds = 60,
        CancellationToken ct = default)
    {
        timeoutSeconds = Math.Clamp(timeoutSeconds, 5, 300);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        int pollCount = 0;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            pollCount++;

            var outboxPending = await paymentsDb.OutboxMessages
                .CountAsync(m => m.Status == Status.Pending || m.Status == Status.Processing, ct);
            var inboxPending = await coreBankDb.InboxMessages
                .CountAsync(m => m.Status == Status.Pending || m.Status == Status.Processing, ct);
            var inboxCompleted = await coreBankDb.InboxMessages
                .CountAsync(m => m.Status == Status.Completed, ct);
            var inboxFailed = await coreBankDb.InboxMessages
                .CountAsync(m => m.Status == Status.Failed, ct);

            if (outboxPending == 0 && inboxPending == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    isDrained = true,
                    pollCount,
                    outboxPending,
                    inboxPending,
                    completed = inboxCompleted,
                    failed = inboxFailed
                });
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        // Timeout reached — return current state
        var finalOutbox = await paymentsDb.OutboxMessages
            .CountAsync(m => m.Status == Status.Pending || m.Status == Status.Processing, CancellationToken.None);
        var finalInbox = await coreBankDb.InboxMessages
            .CountAsync(m => m.Status == Status.Pending || m.Status == Status.Processing, CancellationToken.None);
        var finalCompleted = await coreBankDb.InboxMessages
            .CountAsync(m => m.Status == Status.Completed, CancellationToken.None);
        var finalFailed = await coreBankDb.InboxMessages
            .CountAsync(m => m.Status == Status.Failed, CancellationToken.None);

        return JsonSerializer.Serialize(new
        {
            isDrained = false,
            error = "timeout",
            detail = $"Not drained after {timeoutSeconds}s ({pollCount} polls)",
            pollCount,
            outboxPending = finalOutbox,
            inboxPending = finalInbox,
            completed = finalCompleted,
            failed = finalFailed
        });
    }

    [McpServerTool(Name = "get_assertion_results")]
    [Description(
        "Runs the full assertion suite: verifies exactly-once processing, no duplicates, " +
        "no failures, balance conservation, and correct per-account balances. " +
        "Call this AFTER poll_until_drained reports isDrained=true.")]
    public static async Task<string> GetAssertionResults(
        CoreBankDbContext coreBankDb,
        PaymentsDbContext paymentsDb,
        [Description("Number of unique payments submitted by k6 (e.g. 1000). Used to verify all were processed exactly once.")]
        int expectedUnique,
        CancellationToken ct)
    {
        try
        {
            var completedCount = await coreBankDb.InboxMessages
                .CountAsync(m => m.Status == Status.Completed, ct);
            var failedCount = await coreBankDb.InboxMessages
                .CountAsync(m => m.Status == Status.Failed, ct);
            var pendingCount = await coreBankDb.InboxMessages
                .CountAsync(m => m.Status == Status.Pending || m.Status == Status.Processing, ct);

            var completedInbox = await coreBankDb.InboxMessages
                .Where(m => m.Status == Status.Completed)
                .Select(m => new { m.FromAccount, m.ToAccount, m.Amount, m.IdempotencyKey })
                .ToListAsync(ct);

            var duplicateKeys = await coreBankDb.InboxMessages
                .GroupBy(m => m.IdempotencyKey)
                .Where(g => g.Count() > 1)
                .Select(g => new { Key = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var totalOutbox = await paymentsDb.OutboxMessages.CountAsync(ct);
            var outboxCompleted = await paymentsDb.OutboxMessages
                .CountAsync(m => m.Status == Status.Completed, ct);
            var completedUniqueKeys = completedInbox
                .Select(m => m.IdempotencyKey)
                .Distinct()
                .Count();

            var loadTestAccounts = await coreBankDb.Accounts
                .Where(a => a.AccountNumber.StartsWith("NL") && a.AccountNumber.Contains("LOAD"))
                .OrderBy(a => a.AccountNumber)
                .ToListAsync(ct);

            var totalBalance = loadTestAccounts.Sum(a => a.Balance);
            var expectedTotalBalance = LoadTestAccountCount * InitialBalance;
            var balanceConserved = totalBalance == expectedTotalBalance;

            // Calculate expected per-account balances
            var expectedBalances = new Dictionary<string, decimal>();
            for (int i = 1; i <= LoadTestAccountCount; i++)
                expectedBalances[$"NL{i:D2}LOAD{i:D10}"] = InitialBalance;
            foreach (var tx in completedInbox)
            {
                if (expectedBalances.ContainsKey(tx.FromAccount) && expectedBalances.ContainsKey(tx.ToAccount))
                {
                    expectedBalances[tx.FromAccount] -= tx.Amount;
                    expectedBalances[tx.ToAccount] += tx.Amount;
                }
            }

            var balanceDiscrepancies = loadTestAccounts
                .Where(a => expectedBalances.TryGetValue(a.AccountNumber, out var expected) && a.Balance != expected)
                .Select(a => new
                {
                    account = a.AccountNumber,
                    expected = expectedBalances[a.AccountNumber],
                    actual = a.Balance,
                    difference = a.Balance - expectedBalances[a.AccountNumber]
                })
                .ToList();

            var checks = new
            {
                noFailedMessages = new { passed = failedCount == 0, detail = $"{failedCount} failed" },
                noPendingMessages = new { passed = pendingCount == 0, detail = $"{pendingCount} pending" },
                noDuplicateProcessing = new
                {
                    passed = duplicateKeys.Count == 0,
                    detail = duplicateKeys.Count == 0
                        ? "No duplicates"
                        : $"{duplicateKeys.Count} duplicate key(s)"
                },
                expectedUniqueProcessed = new
                {
                    passed = completedUniqueKeys == expectedUnique,
                    detail = $"expected={expectedUnique}, actual={completedUniqueKeys}"
                },
                allSubmittedProcessed = new
                {
                    passed = completedCount == totalOutbox,
                    detail = $"outbox={totalOutbox}, inboxCompleted={completedCount}"
                },
                balanceConservation = new
                {
                    passed = balanceConserved,
                    detail = $"total={totalBalance:F2}, expected={expectedTotalBalance:F2}"
                },
                balancesCorrect = new
                {
                    passed = balanceDiscrepancies.Count == 0,
                    detail = balanceDiscrepancies.Count == 0
                        ? "All balances match"
                        : $"{balanceDiscrepancies.Count} account(s) mismatched",
                    discrepancies = balanceDiscrepancies
                }
            };

            var allPassed =
                checks.noFailedMessages.passed &&
                checks.noPendingMessages.passed &&
                checks.noDuplicateProcessing.passed &&
                checks.expectedUniqueProcessed.passed &&
                checks.allSubmittedProcessed.passed &&
                checks.balanceConservation.passed &&
                checks.balancesCorrect.passed;

            return JsonSerializer.Serialize(new
            {
                allPassed,
                checks,
                summary = new
                {
                    totalOutbox,
                    outboxCompleted,
                    inboxCompleted = completedCount,
                    inboxFailed = failedCount,
                    inboxPending = pendingCount,
                    completedUniqueKeys,
                    totalBalance,
                    expectedTotalBalance,
                    accountCount = loadTestAccounts.Count
                }
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = "assertion_failed", detail = ex.Message });
        }
    }

    [McpServerTool(Name = "get_corebank_inbox")]
    [Description("Returns recent CoreBank inbox messages. Use to inspect message processing status after a load test.")]
    public static async Task<string> GetCoreBankInbox(
        CoreBankDbContext db,
        [Description("Max messages to return (default 20, max 100)")]
        int limit = 20,
        [Description("Filter by status: Pending, Processing, Completed, or Failed. Omit for all.")]
        string? status = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);

        var query = db.InboxMessages.AsQueryable();
        if (!string.IsNullOrEmpty(status))
            query = query.Where(m => m.Status == status);

        var messages = await query
            .OrderByDescending(m => m.ReceivedAt)
            .Take(limit)
            .Select(m => new
            {
                m.Id,
                m.IdempotencyKey,
                m.Status,
                m.FromAccount,
                m.ToAccount,
                m.Amount,
                m.ReceivedAt,
                m.ProcessedAt,
                m.LastError
            })
            .ToListAsync(ct);

        return JsonSerializer.Serialize(new { count = messages.Count, messages });
    }

    [McpServerTool(Name = "get_corebank_outbox")]
    [Description("Returns recent CoreBank outbox messages (domain events published after transaction processing).")]
    public static async Task<string> GetCoreBankOutbox(
        CoreBankDbContext db,
        [Description("Max messages to return (default 20, max 100)")]
        int limit = 20,
        [Description("Filter by status: Pending, Processing, Completed, or Failed. Omit for all.")]
        string? status = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);

        var query = db.MessagingOutboxMessages.AsQueryable();
        if (!string.IsNullOrEmpty(status))
            query = query.Where(m => m.Status == status);

        var messages = await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .Select(m => new
            {
                m.Id,
                m.TransactionId,
                m.Status,
                m.EventType,
                m.CreatedAt,
                m.ProcessedAt,
                m.LastError
            })
            .ToListAsync(ct);

        return JsonSerializer.Serialize(new { count = messages.Count, messages });
    }

    [McpServerTool(Name = "get_payments_inbox")]
    [Description("Returns recent Payments inbox messages (events received from CoreBank after transaction processing).")]
    public static async Task<string> GetPaymentsInbox(
        PaymentsDbContext db,
        [Description("Max messages to return (default 20, max 100)")]
        int limit = 20,
        [Description("Filter by status: Pending, Processing, Completed, or Failed. Omit for all.")]
        string? status = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);

        var query = db.InboxMessages.AsQueryable();
        if (!string.IsNullOrEmpty(status))
            query = query.Where(m => m.Status == status);

        var messages = await query
            .OrderByDescending(m => m.ReceivedAt)
            .Take(limit)
            .Select(m => new
            {
                m.Id,
                m.IdempotencyKey,
                m.Status,
                m.EventType,
                m.ReceivedAt,
                m.ProcessedAt,
                m.LastError
            })
            .ToListAsync(ct);

        return JsonSerializer.Serialize(new { count = messages.Count, messages });
    }

    [McpServerTool(Name = "get_payments_outbox")]
    [Description("Returns recent Payments outbox messages (payment requests queued for forwarding to CoreBank via Dapr).")]
    public static async Task<string> GetPaymentsOutbox(
        PaymentsDbContext db,
        [Description("Max messages to return (default 20, max 100)")]
        int limit = 20,
        [Description("Filter by status: Pending, Processing, Completed, or Failed. Omit for all.")]
        string? status = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);

        var query = db.OutboxMessages.AsQueryable();
        if (!string.IsNullOrEmpty(status))
            query = query.Where(m => m.Status == status);

        var messages = await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .Select(m => new
            {
                m.Id,
                m.IdempotencyKey,
                m.Status,
                m.FromAccount,
                m.ToAccount,
                m.Amount,
                m.CreatedAt,
                m.ProcessedAt,
                m.LastError
            })
            .ToListAsync(ct);

        return JsonSerializer.Serialize(new { count = messages.Count, messages });
    }
}
