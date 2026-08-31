using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.LoadTestSupport.Services;
using CoreBankDemo.PaymentsAPI;

namespace CoreBankDemo.LoadTestSupport.McpTools;

[McpServerToolType]
public sealed class LoadTestTools
{
    private const decimal InitialBalance = LoadTestConstants.InitialBalance;

    // Matches ASP.NET Core's minimal-API JSON defaults (camelCase) so
    // get_assertion_results/poll_until_drained produce structurally
    // identical output to /assert/results and /assert/drain, per story 7.1.
    private static readonly JsonSerializerOptions McpJsonOptions = new(JsonSerializerDefaults.Web);

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
        "polling every 2 seconds — do NOT call repeatedly. Streams progress notifications with " +
        "percentage and message counts. Returns the final drain status. " +
        "IMPORTANT: pass minimumExpectedCompleted (e.g. 1000) to avoid false 'drained' results " +
        "when k6 is still submitting payments.")]
    public static async Task<string> PollUntilDrained(
        LoadTestAssertionService assertionService,
        IProgress<ProgressNotificationValue> progress,
        [Description("Minimum number of completed inbox messages required before the system can be " +
                     "considered drained. Use this to prevent false positives when k6 is still " +
                     "submitting payments. Set to the expected unique transaction count (e.g. 1000).")]
        int minimumExpectedCompleted = 0,
        [Description("Maximum seconds to wait for drain (default 120, max 300)")]
        int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        timeoutSeconds = Math.Clamp(timeoutSeconds, 5, 300);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        int pollCount = 0;
        int totalMessages = 0;

        try
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                pollCount++;

                var drain = await assertionService.CheckDrainAsync(ct);

                int processed = drain.Completed + drain.Failed;
                int currentTotal = processed + drain.OutboxPending + drain.InboxPending
                    + drain.CoreBankOutboxPending + drain.PaymentsInboxPending;

                // Use the higher of observed total or minimumExpectedCompleted for percentage
                if (currentTotal > totalMessages)
                    totalMessages = currentTotal;
                int effectiveTotal = Math.Max(totalMessages, minimumExpectedCompleted);

                float percentage = effectiveTotal > 0
                    ? Math.Min(processed * 100f / effectiveTotal, 100f)
                    : 0f;

                bool meetsMinimum = processed >= minimumExpectedCompleted;

                progress.Report(new ProgressNotificationValue
                {
                    Progress = percentage,
                    Total = 100,
                    Message = $"Poll {pollCount}: {processed}/{effectiveTotal} processed ({percentage:F0}%), " +
                              $"outbox pending: {drain.OutboxPending}, inbox pending: {drain.InboxPending}, " +
                              $"corebank outbox pending: {drain.CoreBankOutboxPending}, " +
                              $"payments inbox pending: {drain.PaymentsInboxPending}" +
                              (minimumExpectedCompleted > 0 && !meetsMinimum
                                  ? $" [waiting for {minimumExpectedCompleted - processed} more]"
                                  : "")
                });

                if (drain.IsDrained && meetsMinimum)
                {
                    return JsonSerializer.Serialize(new
                    {
                        drain.IsDrained,
                        pollCount,
                        drain.OutboxPending,
                        drain.InboxPending,
                        drain.CoreBankOutboxPending,
                        drain.PaymentsInboxPending,
                        drain.Completed,
                        drain.Failed
                    }, McpJsonOptions);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            // Timeout reached — return current state
            var final = await assertionService.CheckDrainAsync(CancellationToken.None);

            return JsonSerializer.Serialize(new
            {
                isDrained = false,
                error = "timeout",
                detail = $"Not drained after {timeoutSeconds}s ({pollCount} polls)",
                pollCount,
                final.OutboxPending,
                final.InboxPending,
                final.CoreBankOutboxPending,
                final.PaymentsInboxPending,
                final.Completed,
                final.Failed
            }, McpJsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = "drain_check_failed", detail = ex.Message }, McpJsonOptions);
        }
    }

    [McpServerTool(Name = "get_assertion_results")]
    [Description(
        "Runs the full assertion suite: verifies exactly-once processing, no duplicates, " +
        "no failures, balance conservation, and correct per-account balances. " +
        "Call this AFTER poll_until_drained reports isDrained=true.")]
    public static async Task<string> GetAssertionResults(
        LoadTestAssertionService assertionService,
        [Description("Number of unique payments submitted by k6 (e.g. 1000). Used to verify all were processed exactly once.")]
        int expectedUnique,
        CancellationToken ct)
    {
        try
        {
            var result = await assertionService.GetResultsAsync(expectedUnique, ct);
            return JsonSerializer.Serialize(result, McpJsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = "assertion_failed", detail = ex.Message }, McpJsonOptions);
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
    [Description("Returns recent Payments outbox messages (payment requests queued for forwarding to CoreBank via Kiota HTTP).")]
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
