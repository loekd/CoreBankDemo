using Microsoft.EntityFrameworkCore;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.PaymentsAPI;
using static CoreBankDemo.Messaging.MessageConstants;

namespace CoreBankDemo.LoadTestSupport.Services;

/// <summary>
/// Result of polling all four message stores for "drained" status. Field
/// names/meanings for <see cref="OutboxPending"/>, <see cref="InboxPending"/>,
/// <see cref="Completed"/>, and <see cref="Failed"/> are preserved verbatim
/// from the pre-story-7.1 <c>/assert/drain</c> and <c>poll_until_drained</c>
/// responses (k6's <c>script.js</c> and the load-test skill parse them by
/// name); <see cref="CoreBankOutboxPending"/> and
/// <see cref="PaymentsInboxPending"/> are additive.
/// </summary>
public sealed record DrainResult(
    bool IsDrained,
    int OutboxPending,
    int InboxPending,
    int CoreBankOutboxPending,
    int PaymentsInboxPending,
    int Completed,
    int Failed);

/// <summary>One pass/fail invariant check with a human-readable detail string.</summary>
public sealed record AssertionCheck(bool Passed, string Detail);

/// <summary>One idempotency key that appeared more than once in <c>coreBankDb.InboxMessages</c>, with its occurrence count.</summary>
public sealed record DuplicateKeyInfo(string Key, int Count);

/// <summary>One load-test account whose persisted balance diverges from the balance-replay expectation.</summary>
public sealed record BalanceDiscrepancy(
    string AccountNumber,
    decimal Expected,
    decimal Actual,
    decimal Difference);

/// <summary>One completed CoreBank inbox row, projected down to the fields the
/// balance-replay math needs — decoupled from the EF entity so the pure
/// compute path stays Docker-free testable.</summary>
public sealed record CompletedTransaction(
    string FromAccount,
    string ToAccount,
    decimal Amount,
    string IdempotencyKey);

/// <summary>A load-test account's current balance, projected down from
/// <see cref="Account"/> for the same reason as <see cref="CompletedTransaction"/>.</summary>
public sealed record LoadTestAccountBalance(string AccountNumber, decimal Balance);

/// <summary>
/// <paramref name="Id"/> is the row's database identity (the same
/// <c>Id</c> column <c>GetClaimableMessagesQuery</c>'s
/// <c>.OrderBy(m => m.ReceivedAt/CreatedAt).ThenBy(m => m.Id)</c> already
/// uses as the stable tiebreaker for "oldest" selection) -- defaulted so
/// existing 5-arg call sites/tests keep compiling, but required for two
/// same-<paramref name="EnqueuedAt"/> rows in the same partition to be
/// distinguishable and still compared for FIFO violations
/// (<see cref="LoadTestAssertionCalculator.FindOrderingViolations"/>).
/// </summary>
public sealed record OrderingObservation(
    string Store,
    int PartitionId,
    string IdempotencyKey,
    DateTime EnqueuedAt,
    DateTime? ProcessedAt,
    Guid Id = default);

public sealed record OrderingViolation(
    string Store,
    int PartitionId,
    string EarlierKey,
    string LaterKey,
    DateTime EarlierEnqueuedAt,
    DateTime LaterEnqueuedAt,
    DateTime EarlierProcessedAt,
    DateTime LaterProcessedAt);

/// <summary>
/// The <c>NoDuplicateProcessing</c> check plus the actual duplicate rows
/// found. <see cref="Duplicates"/> is nested inside this check object (not a
/// top-level sibling in <see cref="AssertionChecks"/>) to preserve the
/// pre-story-7.1 <c>checks.noDuplicateProcessing.duplicates</c> JSON contract
/// byte-for-byte.
/// </summary>
public sealed record NoDuplicateProcessingCheck(
    bool Passed,
    string Detail,
    IReadOnlyList<DuplicateKeyInfo> Duplicates);

/// <summary>
/// The <c>BalancesCorrect</c> check plus the discrepancies found.
/// <see cref="Discrepancies"/> is nested inside this check object (not a
/// top-level sibling in <see cref="AssertionChecks"/>) to preserve the
/// pre-story-7.1 <c>checks.balancesCorrect.discrepancies</c> JSON contract
/// byte-for-byte — <c>k6/script.js</c> reads it by that exact path.
/// </summary>
public sealed record BalancesCorrectCheck(
    bool Passed,
    string Detail,
    IReadOnlyList<BalanceDiscrepancy> Discrepancies);

/// <summary>Status counts for one durable message store.</summary>
public sealed record MessageStoreSummary(int Total, int Completed, int Failed, int NonTerminal);

/// <summary>Expected and actual message counts at each processing stage.</summary>
public sealed record StageCardinalityCheck(
    bool Passed,
    string Detail,
    int? ExpectedUnique,
    MessageStoreSummary PaymentsOutbox,
    MessageStoreSummary CoreBankInbox,
    MessageStoreSummary CoreBankOutbox,
    MessageStoreSummary PaymentsInbox);

/// <summary>Proof that the database contains exactly the ten seeded load-test accounts.</summary>
public sealed record CanonicalAccountSetCheck(
    bool Passed,
    string Detail,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Unexpected);

/// <summary>The full set of five-invariant assertion checks run against one seeded dataset.</summary>
public sealed record AssertionChecks(
    AssertionCheck NoFailedMessages,
    AssertionCheck NoPendingMessages,
    NoDuplicateProcessingCheck NoDuplicateProcessing,
    AssertionCheck ExpectedUniqueProcessed,
    AssertionCheck AllSubmittedProcessed,
    AssertionCheck BalanceConservation,
    BalancesCorrectCheck BalancesCorrect,
    AssertionCheck PerKeyOrdering,
    AssertionCheck InlineInstantSettlement,
    StageCardinalityCheck StageCardinality,
    CanonicalAccountSetCheck CanonicalAccountSet);

/// <summary>Aggregate counts and totals backing the assertion checks in <see cref="AssertionChecks"/>.</summary>
public sealed record AssertionSummary(
    int TotalOutbox,
    int OutboxCompleted,
    int OutboxPending,
    int InboxCompleted,
    int InboxFailed,
    int InboxPending,
    int OutboxUniqueKeys,
    int CompletedUniqueKeys,
    decimal TotalBalance,
    decimal ExpectedTotalBalance,
    int AccountCount,
    int InlineInstantSettlementCount,
    MessageStoreSummary PaymentsOutbox,
    MessageStoreSummary CoreBankInbox,
    MessageStoreSummary CoreBankOutbox,
    MessageStoreSummary PaymentsInbox);

/// <summary>Raw completed-transaction data behind the balance replay, for troubleshooting a failed assertion run.</summary>
public sealed record AssertionDebugInfo(
    IReadOnlyList<CompletedTransaction> CompletedTransactions,
    IReadOnlyList<OrderingViolation> OrderingViolations);

/// <summary>Full result of the assertion suite: overall pass/fail, the individual checks, aggregate summary, and replay debug data.</summary>
public sealed record AssertionResult(
    bool AllPassed,
    AssertionChecks Checks,
    AssertionSummary Summary,
    AssertionDebugInfo Debug);

/// <summary>
/// Single home for the drain-check and five-invariant/balance-replay logic
/// previously duplicated between <c>AssertEndpoints</c> (REST) and
/// <c>LoadTestTools</c> (MCP) — story 7.1's realignment. EF-backed
/// (<see cref="CheckDrainAsync"/>, <see cref="GetResultsAsync"/>) — this
/// class is a provider-sensitive persistence adapter (ADR-016 tier 2, same
/// category as <c>LoadTestDatabaseResetter</c>) and is covered by the
/// PostgreSQL Testcontainers persistence integration tier, not the
/// Docker-free unit tier. The pure five-invariant/balance-replay math it
/// delegates to lives in <see cref="LoadTestAssertionCalculator"/> instead —
/// a separate type so coverlet's per-type tier filters
/// (<c>tests/Directory.Build.props</c>'s <c>$(PersistenceTierFilters)</c>)
/// can assign each half of this story's extracted logic to the tier that
/// can actually exercise it, mirroring the existing
/// <c>DatabaseResetCoordinator</c>/<c>LoadTestDatabaseResetter</c> split.
/// </summary>
public sealed class LoadTestAssertionService(
    CoreBankDbContext coreBankDb,
    PaymentsDbContext paymentsDb,
    LoadRunEvidenceState? runEvidence = null)
{
    /// <summary>
    /// Polls all four message stores — <c>paymentsDb.OutboxMessages</c>,
    /// <c>paymentsDb.InboxMessages</c>, <c>coreBankDb.InboxMessages</c>, and
    /// <c>coreBankDb.MessagingOutboxMessages</c> — and reports drained only
    /// when every one of them has zero <c>Pending</c>/<c>Processing</c> rows.
    /// </summary>
    public async Task<DrainResult> CheckDrainAsync(CancellationToken ct = default)
    {
        // Payments outbox: messages still waiting to be published via Dapr
        var outboxPending = await paymentsDb.OutboxMessages
            .CountAsync(m => m.Status == Status.Pending || m.Status == Status.Processing, ct);

        // CoreBank inbox: messages received but not yet processed
        var inboxPending = await coreBankDb.InboxMessages
            .CountAsync(m => m.Status == Status.Pending || m.Status == Status.Processing, ct);

        // CoreBank outbox: domain events not yet published via Dapr
        var coreBankOutboxPending = await coreBankDb.MessagingOutboxMessages
            .CountAsync(m => m.Status == Status.Pending || m.Status == Status.Processing, ct);

        // Payments inbox: events received from CoreBank but not yet processed
        var paymentsInboxPending = await paymentsDb.InboxMessages
            .CountAsync(m => m.Status == Status.Pending || m.Status == Status.Processing, ct);

        var completed = await coreBankDb.InboxMessages.CountAsync(m => m.Status == Status.Completed, ct);
        var failed = await coreBankDb.InboxMessages.CountAsync(m => m.Status == Status.Failed, ct);

        var isDrained = outboxPending == 0
            && inboxPending == 0
            && coreBankOutboxPending == 0
            && paymentsInboxPending == 0;

        return new DrainResult(
            isDrained,
            outboxPending,
            inboxPending,
            coreBankOutboxPending,
            paymentsInboxPending,
            completed,
            failed);
    }

    /// <summary>
    /// Runs the full assertion suite. <paramref name="expectedUnique"/> is
    /// nullable to preserve <c>/assert/results</c>'s existing optional-query-
    /// parameter behavior (omitted = the expected-unique check always
    /// passes); <c>get_assertion_results</c> always supplies a value.
    /// </summary>
    public async Task<AssertionResult> GetResultsAsync(int? expectedUnique, CancellationToken ct = default)
    {
        var paymentsOutboxStatuses = await paymentsDb.OutboxMessages.Select(m => m.Status).ToListAsync(ct);
        var coreBankInboxStatuses = await coreBankDb.InboxMessages.Select(m => m.Status).ToListAsync(ct);
        var coreBankOutboxStatuses = await coreBankDb.MessagingOutboxMessages.Select(m => m.Status).ToListAsync(ct);
        var paymentsInboxStatuses = await paymentsDb.InboxMessages.Select(m => m.Status).ToListAsync(ct);

        var paymentsOutboxSummary = Summarize(paymentsOutboxStatuses);
        var coreBankInboxSummary = Summarize(coreBankInboxStatuses);
        var coreBankOutboxSummary = Summarize(coreBankOutboxStatuses);
        var paymentsInboxSummary = Summarize(paymentsInboxStatuses);

        var completedInbox = await coreBankDb.InboxMessages
            .Where(m => m.Status == Status.Completed)
            .Select(m => new CompletedTransaction(m.FromAccount, m.ToAccount, m.Amount, m.IdempotencyKey))
            .ToListAsync(ct);

        var duplicateKeys = await coreBankDb.InboxMessages
            .GroupBy(m => m.IdempotencyKey)
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateKeyInfo(g.Key, g.Count()))
            .ToListAsync(ct);

        var outboxUniqueKeys = await paymentsDb.OutboxMessages
            .Select(m => m.IdempotencyKey)
            .Distinct()
            .CountAsync(ct);

        // Intentionally broad filter (not StartsWith("NL") as well): CanonicalAccountSetCheck
        // below does the exact-match validation against the 10 canonical account numbers, so
        // any stray "contains LOAD" account is reported as unexpected rather than silently
        // dropped from the balance/conservation totals.
        var loadTestAccounts = await coreBankDb.Accounts
            .Where(a => a.AccountNumber.Contains("LOAD"))
            .OrderBy(a => a.AccountNumber)
            .Select(a => new LoadTestAccountBalance(a.AccountNumber, a.Balance))
            .ToListAsync(ct);

        var orderingObservations = new List<OrderingObservation>();
        orderingObservations.AddRange(await paymentsDb.OutboxMessages
            .Select(message => new OrderingObservation(
                "PaymentsOutbox",
                message.PartitionId,
                message.IdempotencyKey,
                message.CreatedAt,
                message.ProcessedAt,
                message.Id))
            .ToListAsync(ct));
        orderingObservations.AddRange(await coreBankDb.InboxMessages
            .Select(message => new OrderingObservation(
                "CoreBankInbox",
                message.PartitionId,
                message.IdempotencyKey,
                message.ReceivedAt,
                message.ProcessedAt,
                message.Id))
            .ToListAsync(ct));
        orderingObservations.AddRange(await coreBankDb.MessagingOutboxMessages
            .Select(message => new OrderingObservation(
                "CoreBankOutbox",
                message.PartitionId,
                message.IdempotencyKey,
                message.CreatedAt,
                message.ProcessedAt,
                message.Id))
            .ToListAsync(ct));
        orderingObservations.AddRange(await paymentsDb.InboxMessages
            .Select(message => new OrderingObservation(
                "PaymentsInbox",
                message.PartitionId,
                message.IdempotencyKey,
                message.ReceivedAt,
                message.ProcessedAt,
                message.Id))
            .ToListAsync(ct));

        var request = new ComputeAssertionRequest(
            ExpectedUnique: expectedUnique,
            PaymentsOutbox: paymentsOutboxSummary,
            CoreBankInbox: coreBankInboxSummary,
            CoreBankOutbox: coreBankOutboxSummary,
            PaymentsInbox: paymentsInboxSummary,
            CompletedTransactions: completedInbox,
            DuplicateKeys: duplicateKeys,
            OutboxUniqueKeys: outboxUniqueKeys,
            LoadTestAccounts: loadTestAccounts,
            OrderingObservations: orderingObservations,
            InlineInstantSettlementCount: runEvidence?.InlineSettlementCount ?? 0);

        return LoadTestAssertionCalculator.ComputeAssertionResult(request);
    }

    private static MessageStoreSummary Summarize(IReadOnlyCollection<string> statuses) => new(
        statuses.Count,
        statuses.Count(status => status == Status.Completed),
        statuses.Count(status => status == Status.Failed),
        statuses.Count(status => status != Status.Completed && status != Status.Failed));
}

/// <summary>
/// Input bundle for <see cref="LoadTestAssertionCalculator.ComputeAssertionResult"/>.
/// Several of the original positional parameters were same-typed adjacent
/// <c>int</c>s (e.g. completed/failed/pending counts, several outbox counts);
/// naming each field here makes the call site in
/// <see cref="LoadTestAssertionService.GetResultsAsync"/> safe against a
/// silent, still-compiling argument transposition.
/// </summary>
internal sealed record ComputeAssertionRequest(
    int? ExpectedUnique,
    MessageStoreSummary PaymentsOutbox,
    MessageStoreSummary CoreBankInbox,
    MessageStoreSummary CoreBankOutbox,
    MessageStoreSummary PaymentsInbox,
    IReadOnlyList<CompletedTransaction> CompletedTransactions,
    IReadOnlyList<DuplicateKeyInfo> DuplicateKeys,
    int OutboxUniqueKeys,
    IReadOnlyList<LoadTestAccountBalance> LoadTestAccounts,
    IReadOnlyList<OrderingObservation>? OrderingObservations = null,
    int InlineInstantSettlementCount = 0);

/// <summary>
/// The pure half of story 7.1's extraction: five-invariant/balance-replay
/// computation over already-fetched data, with no EF/DbContext dependency —
/// Docker-free unit-testable (<c>LoadTestAssertionServiceTests</c>) and, per
/// <c>tests/Directory.Build.props</c>, measured by the unit coverage tier
/// rather than the persistence integration tier that measures
/// <see cref="LoadTestAssertionService"/> itself.
/// </summary>
public static class LoadTestAssertionCalculator
{
    private const decimal InitialBalance = LoadTestConstants.InitialBalance;
    private const int LoadTestAccountCount = LoadTestConstants.AccountCount;

    /// <summary>
    /// Pure five-invariant/balance-replay computation over already-fetched
    /// data — no EF/DB access, Docker-free unit-testable.
    /// </summary>
    internal static AssertionResult ComputeAssertionResult(ComputeAssertionRequest request)
    {
        var expectedUnique = request.ExpectedUnique;
        var paymentsOutbox = request.PaymentsOutbox;
        var coreBankInbox = request.CoreBankInbox;
        var coreBankOutbox = request.CoreBankOutbox;
        var paymentsInbox = request.PaymentsInbox;
        var completedTransactions = request.CompletedTransactions;
        var duplicateKeys = request.DuplicateKeys;
        var outboxUniqueKeys = request.OutboxUniqueKeys;
        var loadTestAccounts = request.LoadTestAccounts;
        var orderingObservations = request.OrderingObservations ?? [];

        var completedUniqueKeys = completedTransactions
            .Select(t => t.IdempotencyKey)
            .Distinct()
            .Count();

        var totalBalance = loadTestAccounts.Sum(a => a.Balance);
        var expectedTotalBalance = LoadTestAccountCount * InitialBalance;
        var balanceConserved = totalBalance == expectedTotalBalance;

        var expectedBalances = CalculateExpectedBalances(completedTransactions);

        var balanceDiscrepancies = new List<BalanceDiscrepancy>();
        foreach (var account in loadTestAccounts)
        {
            if (expectedBalances.TryGetValue(account.AccountNumber, out var expectedBalance)
                && account.Balance != expectedBalance)
            {
                balanceDiscrepancies.Add(new BalanceDiscrepancy(
                    account.AccountNumber,
                    expectedBalance,
                    account.Balance,
                    account.Balance - expectedBalance));
            }
        }

        var balancesCorrect = balanceDiscrepancies.Count == 0;

        var failedCount = paymentsOutbox.Failed + coreBankInbox.Failed + coreBankOutbox.Failed + paymentsInbox.Failed;
        var pendingCount = paymentsOutbox.NonTerminal + coreBankInbox.NonTerminal
            + coreBankOutbox.NonTerminal + paymentsInbox.NonTerminal;
        var noFailedMessages = new AssertionCheck(
            failedCount == 0,
            $"{failedCount} failed message(s); Failed: PaymentsOutbox={paymentsOutbox.Failed}, CoreBankInbox={coreBankInbox.Failed}, CoreBankOutbox={coreBankOutbox.Failed}, PaymentsInbox={paymentsInbox.Failed}");
        var noPendingMessages = new AssertionCheck(
            pendingCount == 0,
            $"{pendingCount} still pending/processing; NonTerminal: PaymentsOutbox={paymentsOutbox.NonTerminal}, CoreBankInbox={coreBankInbox.NonTerminal}, CoreBankOutbox={coreBankOutbox.NonTerminal}, PaymentsInbox={paymentsInbox.NonTerminal}");
        var noDuplicateProcessing = new NoDuplicateProcessingCheck(
            duplicateKeys.Count == 0,
            duplicateKeys.Count == 0
                ? "No duplicates"
                : $"{duplicateKeys.Count} duplicate key(s): {string.Join(", ", duplicateKeys.Select(d => $"{d.Key}(x{d.Count})"))}",
            duplicateKeys);
        var expectedUniqueProcessed = new AssertionCheck(
            !expectedUnique.HasValue || completedUniqueKeys == expectedUnique.Value,
            expectedUnique.HasValue
                ? $"ExpectedUnique={expectedUnique.Value}, CompletedUnique={completedUniqueKeys}"
                : $"CompletedUnique={completedUniqueKeys}");
        var allSubmittedProcessed = new AssertionCheck(
            coreBankInbox.Completed == paymentsOutbox.Total,
            $"OutboxTotal={paymentsOutbox.Total}, InboxCompleted={coreBankInbox.Completed}");
        var balanceConservation = new AssertionCheck(
            balanceConserved,
            $"Total={totalBalance:F2}, Expected={expectedTotalBalance:F2}");
        var balancesCorrectCheck = new BalancesCorrectCheck(
            balancesCorrect,
            balancesCorrect
                ? "All balances match expected values"
                : $"{balanceDiscrepancies.Count} account(s) have incorrect balances",
            balanceDiscrepancies);
        var orderingViolations = FindOrderingViolations(orderingObservations);
        var missingProcessedAt = orderingObservations.Count(item => !item.ProcessedAt.HasValue);
        var perKeyOrdering = new AssertionCheck(
            orderingObservations.Count > 0 && missingProcessedAt == 0 && orderingViolations.Count == 0,
            orderingObservations.Count == 0
                ? "No durable ordering observations were available"
                : missingProcessedAt > 0
                    ? $"{missingProcessedAt} ordering observation(s) have no ProcessedAt timestamp"
                : orderingViolations.Count == 0
                    ? $"Verified timestamp-distinct FIFO ordering across {orderingObservations.Select(item => (item.Store, item.PartitionId)).Distinct().Count()} store partitions"
                    : $"{orderingViolations.Count} ordering inversion(s); first: {orderingViolations[0].Store}/partition-{orderingViolations[0].PartitionId} {orderingViolations[0].EarlierKey} processed after {orderingViolations[0].LaterKey}");
        var inlineInstantSettlement = new AssertionCheck(
            request.InlineInstantSettlementCount > 0,
            $"Fresh instant payments completed inline: {request.InlineInstantSettlementCount}");

        var cardinalityPassed = !expectedUnique.HasValue ||
            (paymentsOutbox.Total == expectedUnique.Value
             && paymentsOutbox.Completed == expectedUnique.Value
             && coreBankInbox.Total == expectedUnique.Value
             && coreBankInbox.Completed == expectedUnique.Value
             && coreBankOutbox.Total == expectedUnique.Value * 3
             && coreBankOutbox.Completed == expectedUnique.Value * 3
             && paymentsInbox.Total == expectedUnique.Value * 3
             && paymentsInbox.Completed == expectedUnique.Value * 3);
        var stageCardinality = new StageCardinalityCheck(
            cardinalityPassed,
            expectedUnique.HasValue
                ? $"Expected N/N/3N/3N={expectedUnique.Value}/{expectedUnique.Value}/{expectedUnique.Value * 3}/{expectedUnique.Value * 3}; Actual={paymentsOutbox.Total}/{coreBankInbox.Total}/{coreBankOutbox.Total}/{paymentsInbox.Total}"
                : "ExpectedUnique was not supplied",
            expectedUnique,
            paymentsOutbox,
            coreBankInbox,
            coreBankOutbox,
            paymentsInbox);

        var expectedAccounts = Enumerable.Range(1, LoadTestAccountCount)
            .Select(i => $"NL{i:D2}LOAD{i:D10}")
            .ToHashSet(StringComparer.Ordinal);
        var actualAccounts = loadTestAccounts.Select(account => account.AccountNumber).ToHashSet(StringComparer.Ordinal);
        var missingAccounts = expectedAccounts.Except(actualAccounts).Order().ToArray();
        var unexpectedAccounts = actualAccounts.Except(expectedAccounts).Order().ToArray();
        var canonicalAccountSet = new CanonicalAccountSetCheck(
            missingAccounts.Length == 0 && unexpectedAccounts.Length == 0,
            $"Expected {LoadTestAccountCount} canonical accounts; actual={actualAccounts.Count}, missing={missingAccounts.Length}, unexpected={unexpectedAccounts.Length}",
            missingAccounts,
            unexpectedAccounts);

        var checks = new AssertionChecks(
            noFailedMessages,
            noPendingMessages,
            noDuplicateProcessing,
            expectedUniqueProcessed,
            allSubmittedProcessed,
            balanceConservation,
            balancesCorrectCheck,
            perKeyOrdering,
            inlineInstantSettlement,
            stageCardinality,
            canonicalAccountSet);

        var allPassed =
            noFailedMessages.Passed &&
            noPendingMessages.Passed &&
            noDuplicateProcessing.Passed &&
            expectedUniqueProcessed.Passed &&
            allSubmittedProcessed.Passed &&
            balanceConservation.Passed &&
            balancesCorrectCheck.Passed &&
            perKeyOrdering.Passed &&
            inlineInstantSettlement.Passed &&
            stageCardinality.Passed &&
            canonicalAccountSet.Passed;

        var summary = new AssertionSummary(
            paymentsOutbox.Total,
            paymentsOutbox.Completed,
            paymentsOutbox.NonTerminal,
            coreBankInbox.Completed,
            coreBankInbox.Failed,
            coreBankInbox.NonTerminal,
            outboxUniqueKeys,
            completedUniqueKeys,
            totalBalance,
            expectedTotalBalance,
            loadTestAccounts.Count,
            request.InlineInstantSettlementCount,
            paymentsOutbox,
            coreBankInbox,
            coreBankOutbox,
            paymentsInbox);

        return new AssertionResult(
            allPassed,
            checks,
            summary,
            new AssertionDebugInfo(completedTransactions, orderingViolations));
    }

    internal static IReadOnlyList<OrderingViolation> FindOrderingViolations(
        IReadOnlyList<OrderingObservation> observations)
    {
        var violations = new List<OrderingViolation>();
        foreach (var partition in observations.GroupBy(item => (item.Store, item.PartitionId)))
        {
            OrderingObservation? latestPrior = null;
            // Grouping by EnqueuedAt alone collapsed every row sharing an
            // exact timestamp (realistic under real concurrent load, since
            // timestamps come from TimeProvider.GetUtcNow() per-request
            // rather than a monotonic sequence) into one group that was
            // never compared against itself -- silently reducing detection
            // power exactly during the highest-throughput phase this check
            // exists to police. Id is the same stable secondary tiebreaker
            // GetClaimableMessagesQuery already uses
            // (.OrderBy(EnqueuedAt).ThenBy(Id)) to give same-timestamp rows a
            // deterministic order, so grouping by (EnqueuedAt, Id) instead
            // makes every row its own step again and restores comparability.
            foreach (var enqueueGroup in partition
                         .OrderBy(item => item.EnqueuedAt)
                         .ThenBy(item => item.Id)
                         .GroupBy(item => (item.EnqueuedAt, item.Id)))
            {
                var current = enqueueGroup
                    .Where(item => item.ProcessedAt.HasValue)
                    .OrderBy(item => item.ProcessedAt)
                    .ToList();
                if (latestPrior?.ProcessedAt is { } priorProcessed
                    && current.FirstOrDefault()?.ProcessedAt is { } currentProcessed
                    && currentProcessed < priorProcessed)
                {
                    var later = current[0];
                    violations.Add(new OrderingViolation(
                        partition.Key.Store,
                        partition.Key.PartitionId,
                        latestPrior.IdempotencyKey,
                        later.IdempotencyKey,
                        latestPrior.EnqueuedAt,
                        later.EnqueuedAt,
                        priorProcessed,
                        currentProcessed));
                }

                var latestCurrent = current.LastOrDefault();
                if (latestCurrent?.ProcessedAt is not null
                    && (latestPrior?.ProcessedAt is null || latestCurrent.ProcessedAt > latestPrior.ProcessedAt))
                {
                    latestPrior = latestCurrent;
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Replays completed transactions on top of each load-test account's
    /// initial balance to compute the expected per-account balance. Moved
    /// unchanged from <c>AssertEndpoints.CalculateExpectedBalances</c> —
    /// algorithm untouched, only its location and callers changed.
    /// </summary>
    internal static Dictionary<string, decimal> CalculateExpectedBalances(
        IReadOnlyList<CompletedTransaction> completedTransactions)
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
