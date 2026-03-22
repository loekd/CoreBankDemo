using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.LoadTestSupport.Endpoints;

public static class AssertEndpoints
{
    private const decimal InitialBalance = 10_000_000.00m;
    private const int LoadTestAccountCount = 10;

    public static void MapAssertEndpoints(this IEndpointRouteBuilder app)
    {
        // Reset database to clean state for load testing
        app.MapPost("/reset", async (CoreBankReadDbContext coreBankDb, PaymentsReadDbContext paymentsDb, CancellationToken ct) =>
        {
            // Truncate all inbox/outbox tables in both databases
            await paymentsDb.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"OutboxMessages\" RESTART IDENTITY CASCADE", ct);
            await paymentsDb.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"InboxMessages\" RESTART IDENTITY CASCADE", ct);
            await coreBankDb.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"InboxMessages\" RESTART IDENTITY CASCADE", ct);
            await coreBankDb.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"MessagingOutboxMessages\" RESTART IDENTITY CASCADE", ct);

            // Reset all load test accounts to initial balance
            var loadTestAccounts = await coreBankDb.Accounts
                .Where(a => a.AccountNumber.StartsWith("NL") && a.AccountNumber.Contains("LOAD"))
                .ToListAsync(ct);

            foreach (var account in loadTestAccounts)
            {
                account.Balance = InitialBalance;
                account.UpdatedAt = null;
            }

            await coreBankDb.Database.ExecuteSqlRawAsync(
                "UPDATE \"Accounts\" SET \"Balance\" = {0}, \"UpdatedAt\" = NULL WHERE \"AccountNumber\" LIKE '%LOAD%'",
                InitialBalance);

            var accountCount = loadTestAccounts.Count;
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

            // Debug: Log all completed transactions for analysis
            var completedInbox = allInbox.Where(m => m.Status == "Completed")
                .Select(m => new { m.FromAccount, m.ToAccount, m.Amount, m.IdempotencyKey })
                .ToList();

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

            // Account balances and verification
            var loadTestAccounts = await coreBankDb.Accounts
                .Where(a => a.AccountNumber.StartsWith("NL") && a.AccountNumber.Contains("LOAD"))
                .OrderBy(a => a.AccountNumber)
                .ToListAsync(ct);

            var totalBalance = loadTestAccounts.Sum(a => a.Balance);
            var expectedTotalBalance = LoadTestAccountCount * InitialBalance;
            var balanceConserved = totalBalance == expectedTotalBalance;

            // Calculate expected balances based on completed transactions
            // Pattern: account i sends to account (i+1) mod 10
            var expectedBalances = CalculateExpectedBalances(allInbox.Where(m => m.Status == "Completed").ToList());

            var balanceDiscrepancies = new List<object>();
            foreach (var account in loadTestAccounts)
            {
                if (expectedBalances.TryGetValue(account.AccountNumber, out var expectedBalance))
                {
                    if (account.Balance != expectedBalance)
                    {
                        balanceDiscrepancies.Add(new
                        {
                            AccountNumber = account.AccountNumber,
                            Expected = expectedBalance,
                            Actual = account.Balance,
                            Difference = account.Balance - expectedBalance
                        });
                    }
                }
            }

            var balancesCorrect = balanceDiscrepancies.Count == 0;

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
                },
                BalanceConservation = new
                {
                    Passed = balanceConserved,
                    Detail = $"Total={totalBalance:F2}, Expected={expectedTotalBalance:F2}"
                },
                BalancesCorrect = new
                {
                    Passed = balancesCorrect,
                    Detail = balancesCorrect
                        ? "All balances match expected values"
                        : $"{balanceDiscrepancies.Count} account(s) have incorrect balances",
                    Discrepancies = balanceDiscrepancies
                }
            };

            var allPassed =
                checks.NoFailedMessages.Passed &&
                checks.NoPendingMessages.Passed &&
                checks.NoDuplicateProcessing.Passed &&
                checks.AllSubmittedProcessed.Passed &&
                checks.BalanceConservation.Passed &&
                checks.BalancesCorrect.Passed;

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
                    ExpectedTotalBalance = expectedTotalBalance,
                    AccountCount = loadTestAccounts.Count
                },
                Debug = new
                {
                    CompletedTransactions = completedInbox
                }
            });
        })
        .WithName("AssertResults")
        .WithSummary("Full assertion suite: exactly-once, no duplicates, no failures, correct balances");
    }

    private static Dictionary<string, decimal> CalculateExpectedBalances(List<CoreBankAPI.Inbox.InboxMessage> completedTransactions)
    {
        var balances = new Dictionary<string, decimal>();

        // Start with initial balances for all load test accounts
        for (int i = 1; i <= LoadTestAccountCount; i++)
        {
            balances[$"NL{i:D2}LOAD{i:D10}"] = InitialBalance;
        }

        // Apply all completed transactions
        foreach (var tx in completedTransactions)
        {
            if (balances.ContainsKey(tx.FromAccount) && balances.ContainsKey(tx.ToAccount))
            {
                balances[tx.FromAccount] -= tx.Amount;
                balances[tx.ToAccount] += tx.Amount;
            }
        }

        return balances;
    }
}



