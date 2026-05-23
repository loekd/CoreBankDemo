using Microsoft.EntityFrameworkCore;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.LoadTestSupport;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using static CoreBankDemo.Messaging.MessageConstants;

namespace CoreBankDemo.LoadTestSupport.Endpoints;

public static class AssertEndpoints
{
    private const decimal InitialBalance = LoadTestConstants.InitialBalance;
    private const int LoadTestAccountCount = LoadTestConstants.AccountCount;

    public static void MapAssertEndpoints(this IEndpointRouteBuilder app)
    {
        // Poll this until all outbox messages are published AND all inbox messages are processed
        app.MapGet("/assert/drain", async (CoreBankDbContext coreBankDb, PaymentsDbContext paymentsDb, CancellationToken ct) =>
        {
            // Payments outbox: messages still waiting to be published via Dapr
            var outboxPending = await paymentsDb.OutboxMessages
                .CountAsync(m => m.Status == Status.Pending || m.Status == Status.Processing, ct);

            // CoreBank inbox: messages received but not yet processed
            var inboxPending = await coreBankDb.InboxMessages
                .CountAsync(m => m.Status == Status.Pending || m.Status == Status.Processing, ct);

            var inboxFailed = await coreBankDb.InboxMessages
                .CountAsync(m => m.Status == Status.Failed, ct);

            var inboxCompleted = await coreBankDb.InboxMessages
                .CountAsync(m => m.Status == Status.Completed, ct);

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
            int? expectedUnique,
            CoreBankDbContext coreBankDb,
            PaymentsDbContext paymentsDb,
            CancellationToken ct) =>
        {
            // Inbox stats from CoreBank — use DB-level queries instead of loading all rows
            var completedCount = await coreBankDb.InboxMessages.CountAsync(m => m.Status == Status.Completed, ct);
            var failedCount = await coreBankDb.InboxMessages.CountAsync(m => m.Status == Status.Failed, ct);
            var pendingCount = await coreBankDb.InboxMessages
                .CountAsync(m => m.Status == Status.Pending || m.Status == Status.Processing, ct);

            // Only load completed transactions (needed for balance verification)
            var completedInbox = await coreBankDb.InboxMessages
                .Where(m => m.Status == Status.Completed)
                .Select(m => new CompletedTransaction(m.FromAccount, m.ToAccount, m.Amount, m.IdempotencyKey))
                .ToListAsync(ct);

            // Duplicate detection: same idempotency key appearing more than once
            var duplicateKeys = await coreBankDb.InboxMessages
                .GroupBy(m => m.IdempotencyKey)
                .Where(g => g.Count() > 1)
                .Select(g => new { Key = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            // Outbox stats from Payments — each row is one unique payment submission
            var totalOutbox = await paymentsDb.OutboxMessages.CountAsync(ct);
            var outboxCompleted = await paymentsDb.OutboxMessages.CountAsync(m => m.Status == Status.Completed, ct);
            var outboxPending = await paymentsDb.OutboxMessages.CountAsync(m => m.Status == Status.Pending || m.Status == Status.Processing, ct);
            var outboxUniqueKeys = await paymentsDb.OutboxMessages
                .Select(m => m.IdempotencyKey)
                .Distinct()
                .CountAsync(ct);
            var completedUniqueKeys = completedInbox
                .Select(m => m.IdempotencyKey)
                .Distinct()
                .Count();

            // Account balances and verification
            var loadTestAccounts = await coreBankDb.Accounts
                .Where(a => a.AccountNumber.StartsWith("NL") && a.AccountNumber.Contains("LOAD"))
                .OrderBy(a => a.AccountNumber)
                .ToListAsync(ct);

            var totalBalance = loadTestAccounts.Sum(a => a.Balance);
            var expectedTotalBalance = LoadTestAccountCount * InitialBalance;
            var balanceConserved = totalBalance == expectedTotalBalance;

            // Calculate expected balances based on completed transactions
            var expectedBalances = CalculateExpectedBalances(completedInbox);

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
                ExpectedUniqueProcessed = new
                {
                    Passed = !expectedUnique.HasValue || completedUniqueKeys == expectedUnique.Value,
                    Detail = expectedUnique.HasValue
                        ? $"ExpectedUnique={expectedUnique.Value}, CompletedUnique={completedUniqueKeys}"
                        : $"CompletedUnique={completedUniqueKeys}"
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
                checks.ExpectedUniqueProcessed.Passed &&
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
                    OutboxUniqueKeys = outboxUniqueKeys,
                    CompletedUniqueKeys = completedUniqueKeys,
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

    private record CompletedTransaction(string FromAccount, string ToAccount, decimal Amount, string IdempotencyKey);

    private static Dictionary<string, decimal> CalculateExpectedBalances(List<CompletedTransaction> completedTransactions)
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


