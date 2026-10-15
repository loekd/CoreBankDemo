using Microsoft.EntityFrameworkCore;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.Messaging;
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
    int Failed,
    // Additive (spec: instant-rail-timeout-cancel): payments outbox rows the
    // instant rail withdrew -- terminal, so they count as processed for
    // drain progress, never as pending. Outbox, not CoreBank inbox: a
    // locally cancelled payment leaves no CoreBank row at all.
    int Cancelled = 0);

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
    Guid Id = default,
    int Priority = 0,
    // spec: instant-rail-timeout-cancel. A Cancelled row never executed, so
    // it has no place in execution order: FindOrderingViolations leaves it
    // out of the FIFO comparison (it still counts as terminal with a
    // ProcessedAt for the missing-timestamp check). Defaulted so existing
    // call sites keep compiling.
    string? Status = null,
    // ADR-026: the debtor account of a payment command (payments outbox,
    // CoreBank inbox) -- the partition key of those two stores. Null for the
    // event stores, which partition on the transaction id or account number
    // and are judged by neither of the per-account checks.
    string? FromAccount = null,
    // When the row became visible to its lane: the commit of the accepting
    // transaction. EnqueuedAt is stamped before that transaction opens, so a
    // row stamped earlier can become visible later than a younger one. A lane
    // can only order what it can see, so FIFO is only owed between rows whose
    // request windows did not overlap: X must precede Y when X was accepted
    // before Y was stamped. Null (the CoreBank outbox, the one store without
    // a commit timestamp) means EnqueuedAt itself, i.e. plain stamp order.
    DateTime? AcceptedAt = null);

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
/// Two debits from one account, in one priority class, processed in the
/// reverse of their enqueue order (ADR-026) -- the inversion key partitioning
/// let through whenever the two rows landed in different partitions.
/// </summary>
public sealed record AccountOrderingViolation(
    string Store,
    string FromAccount,
    int Priority,
    string EarlierKey,
    string LaterKey,
    DateTime EarlierEnqueuedAt,
    DateTime LaterEnqueuedAt,
    DateTime EarlierProcessedAt,
    DateTime LaterProcessedAt);

/// <summary>
/// A payment-command row whose <c>PartitionId</c> is not
/// <see cref="PartitionHelper"/>'s partition for its debtor account (ADR-026).
/// Per-partition FIFO plus correct routing together imply per-account FIFO;
/// this names a routing regression on its own.
/// </summary>
public sealed record PartitionRoutingViolation(
    string Store,
    string IdempotencyKey,
    string FromAccount,
    int PartitionId,
    int ExpectedPartitionId);

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

/// <summary>
/// Status counts for one durable message store. <paramref name="Cancelled"/>
/// (spec: instant-rail-timeout-cancel) is terminal: it is never part of
/// <paramref name="NonTerminal"/>, and <c>Total == Completed + Failed + Cancelled + NonTerminal</c>.
/// </summary>
public sealed record MessageStoreSummary(int Total, int Completed, int Failed, int NonTerminal, int Cancelled = 0);

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
    // ADR-026 (additive): FIFO per debtor account within a priority class,
    // and every command row in its debtor account's partition.
    AssertionCheck PerAccountOrdering,
    AssertionCheck PartitionRouting,
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
    IReadOnlyList<OrderingViolation> OrderingViolations,
    IReadOnlyList<AccountOrderingViolation> AccountOrderingViolations,
    IReadOnlyList<PartitionRoutingViolation> PartitionRoutingViolations);

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
        // Counted on the payments outbox, not CoreBank's inbox: every
        // cancellation -- local (never reached CoreBank), through CoreBank,
        // or replayed to the background rail -- ends as a Cancelled outbox
        // row, whereas only some leave a CoreBank row behind. Counting the
        // inbox would leave locally cancelled payments out of "processed"
        // and time poll_until_drained out on a fully drained system.
        var cancelled = await paymentsDb.OutboxMessages.CountAsync(m => m.Status == Status.Cancelled, ct);

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
            failed,
            cancelled);
    }

    /// <summary>
    /// The accepting transaction's commit timestamp, which every commit-ordered
    /// store must have: a missing one means the load-test PostgreSQL runs
    /// without <c>track_commit_timestamp=on</c>, and that fails loudly rather
    /// than silently falling back to stamp order.
    /// </summary>
    private static DateTime RequireAcceptedAt(string store, string idempotencyKey, DateTimeOffset? acceptedAt) =>
        acceptedAt?.UtcDateTime ?? throw new InvalidOperationException(
            $"{store} row {idempotencyKey} has no commit timestamp; the load-test PostgreSQL must run with track_commit_timestamp=on.");

    /// <summary>Shape of the raw commit-order query over the Payments outbox; column names match the SELECT list.</summary>
    private sealed class PaymentsOutboxOrderingRow
    {
        public int PartitionId { get; set; }
        public required string IdempotencyKey { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTimeOffset? AcceptedAt { get; set; }
        public DateTime? ProcessedAt { get; set; }
        public Guid Id { get; set; }
        public int Priority { get; set; }
        public required string Status { get; set; }
        public required string FromAccount { get; set; }
    }

    /// <summary>Shape of the raw commit-order query over the CoreBank inbox; column names match the SELECT list.</summary>
    private sealed class CoreBankInboxOrderingRow
    {
        public int PartitionId { get; set; }
        public required string IdempotencyKey { get; set; }
        public DateTime ReceivedAt { get; set; }
        public DateTimeOffset? AcceptedAt { get; set; }
        public DateTime? ProcessedAt { get; set; }
        public Guid Id { get; set; }
        public int Priority { get; set; }
        public required string Status { get; set; }
        public required string FromAccount { get; set; }
    }

    /// <summary>Shape of the raw commit-order query over the Payments inbox; column names match the SELECT list.</summary>
    private sealed class PaymentsInboxOrderingRow
    {
        public int PartitionId { get; set; }
        public required string IdempotencyKey { get; set; }
        public DateTime ReceivedAt { get; set; }
        public DateTimeOffset? AcceptedAt { get; set; }
        public DateTime? ProcessedAt { get; set; }
        public Guid Id { get; set; }
        public int Priority { get; set; }
        public required string Status { get; set; }
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
        // The Payments outbox carries the commit timestamp of its accepting
        // transaction as AcceptedAt (docs/constraints.md: "in the order they
        // were accepted"). CreatedAt is stamped before that transaction opens
        // and the lane can only see committed rows, so FIFO is owed only
        // between rows whose request windows did not overlap. Requires
        // track_commit_timestamp=on (LoadTests AppHost, Testcontainers fixture).
        var paymentsOutboxRows = await paymentsDb.Database
            .SqlQuery<PaymentsOutboxOrderingRow>($"""
                SELECT "PartitionId", "IdempotencyKey", "CreatedAt",
                       pg_xact_commit_timestamp(("AcceptedXid" % 4294967296)::text::xid) AS "AcceptedAt",
                       "ProcessedAt", "Id", "Priority", "Status", "FromAccount"
                FROM "OutboxMessages"
                """)
            .ToListAsync(ct);
        orderingObservations.AddRange(paymentsOutboxRows.Select(row => new OrderingObservation(
            "PaymentsOutbox",
            row.PartitionId,
            row.IdempotencyKey,
            row.CreatedAt,
            row.ProcessedAt,
            row.Id,
            row.Priority,
            row.Status,
            row.FromAccount,
            RequireAcceptedAt("PaymentsOutbox", row.IdempotencyKey, row.AcceptedAt))));
        // Both inbox stores carry the same commit timestamp: their intake handlers
        // stamp ReceivedAt before the insert commits, and the three events of one
        // transaction (Payments) or two intakes (CoreBank) arrive concurrently.
        var coreBankInboxRows = await coreBankDb.Database
            .SqlQuery<CoreBankInboxOrderingRow>($"""
                SELECT "PartitionId", "IdempotencyKey", "ReceivedAt",
                       pg_xact_commit_timestamp(("AcceptedXid" % 4294967296)::text::xid) AS "AcceptedAt",
                       "ProcessedAt", "Id", "Priority", "Status", "FromAccount"
                FROM "InboxMessages"
                """)
            .ToListAsync(ct);
        orderingObservations.AddRange(coreBankInboxRows.Select(row => new OrderingObservation(
            "CoreBankInbox",
            row.PartitionId,
            row.IdempotencyKey,
            row.ReceivedAt,
            row.ProcessedAt,
            row.Id,
            row.Priority,
            row.Status,
            row.FromAccount,
            RequireAcceptedAt("CoreBankInbox", row.IdempotencyKey, row.AcceptedAt))));
        orderingObservations.AddRange(await coreBankDb.MessagingOutboxMessages
            .Select(message => new OrderingObservation(
                "CoreBankOutbox",
                message.PartitionId,
                message.IdempotencyKey,
                message.CreatedAt,
                message.ProcessedAt,
                message.Id,
                message.Priority,
                message.Status))
            .ToListAsync(ct));
        var paymentsInboxRows = await paymentsDb.Database
            .SqlQuery<PaymentsInboxOrderingRow>($"""
                SELECT "PartitionId", "IdempotencyKey", "ReceivedAt",
                       pg_xact_commit_timestamp(("AcceptedXid" % 4294967296)::text::xid) AS "AcceptedAt",
                       "ProcessedAt", "Id", "Priority", "Status"
                FROM "InboxMessages"
                """)
            .ToListAsync(ct);
        orderingObservations.AddRange(paymentsInboxRows.Select(row => new OrderingObservation(
            "PaymentsInbox",
            row.PartitionId,
            row.IdempotencyKey,
            row.ReceivedAt,
            row.ProcessedAt,
            row.Id,
            row.Priority,
            row.Status,
            AcceptedAt: RequireAcceptedAt("PaymentsInbox", row.IdempotencyKey, row.AcceptedAt))));

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

    /// <summary>
    /// Terminal is <c>Completed</c>, <c>Failed</c> or <c>Cancelled</c>; everything
    /// else (<c>Pending</c>/<c>Processing</c>) is non-terminal and blocks the gate.
    /// </summary>
    internal static MessageStoreSummary Summarize(IReadOnlyCollection<string> statuses) => new(
        statuses.Count,
        statuses.Count(status => status == Status.Completed),
        statuses.Count(status => status == Status.Failed),
        statuses.Count(status => status != Status.Completed && status != Status.Failed && status != Status.Cancelled),
        statuses.Count(status => status == Status.Cancelled));
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
        // spec: instant-rail-timeout-cancel. A cancelled instant payment is a
        // terminal, provably-not-executed outcome: it reaches a terminal state
        // (zero message loss holds), but it is not "processed" in the ledger
        // sense. So every submitted key is either completed or cancelled, and
        // only the completed ones flow downstream as N completed inbox rows
        // and 3 events each -- plus one transaction.cancelled event per
        // CoreBank-side cancellation (spec: instant-rail-cancelled-event).
        // Detail strings stay byte-identical to the pre-cancellation contract
        // whenever no row was cancelled; the extra counts appear only when
        // they are non-zero.
        var cancelledDetail = paymentsOutbox.Cancelled > 0 || coreBankInbox.Cancelled > 0
            ? $", OutboxCancelled={paymentsOutbox.Cancelled}, InboxCancelled={coreBankInbox.Cancelled}"
            : string.Empty;
        var expectedUniqueProcessed = new AssertionCheck(
            !expectedUnique.HasValue || completedUniqueKeys + paymentsOutbox.Cancelled == expectedUnique.Value,
            (expectedUnique.HasValue
                ? $"ExpectedUnique={expectedUnique.Value}, CompletedUnique={completedUniqueKeys}"
                : $"CompletedUnique={completedUniqueKeys}") + cancelledDetail);
        var allSubmittedProcessed = new AssertionCheck(
            coreBankInbox.Completed == paymentsOutbox.Completed
            && paymentsOutbox.Total == paymentsOutbox.Completed + paymentsOutbox.Cancelled,
            $"OutboxTotal={paymentsOutbox.Total}, OutboxCompleted={paymentsOutbox.Completed}, InboxCompleted={coreBankInbox.Completed}{cancelledDetail}");
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
                    ? $"Verified timestamp-distinct FIFO ordering within each priority class across {orderingObservations.Select(item => (item.Store, item.PartitionId)).Distinct().Count()} store partitions"
                    : $"{orderingViolations.Count} ordering inversion(s); first: {orderingViolations[0].Store}/partition-{orderingViolations[0].PartitionId} {orderingViolations[0].EarlierKey} processed after {orderingViolations[0].LaterKey}");
        // ADR-026: both checks judge only the payment-command rows (the ones
        // carrying a FromAccount). Zero of them is no proof, as above.
        var commandObservations = orderingObservations.Count(item => item.FromAccount is not null);
        var accountOrderingViolations = FindAccountOrderingViolations(orderingObservations);
        var perAccountOrdering = new AssertionCheck(
            commandObservations > 0 && accountOrderingViolations.Count == 0,
            commandObservations == 0
                ? "No payment-command observations were available"
                : accountOrderingViolations.Count == 0
                    ? $"Verified FIFO per debtor account within each priority class across {orderingObservations.Where(item => item.FromAccount is not null).Select(item => (item.Store, item.FromAccount)).Distinct().Count()} account lanes"
                    : $"{accountOrderingViolations.Count} per-account inversion(s); first: {accountOrderingViolations[0].Store}/{accountOrderingViolations[0].FromAccount} {accountOrderingViolations[0].EarlierKey} processed after {accountOrderingViolations[0].LaterKey}");
        var partitionRoutingViolations = FindPartitionRoutingViolations(orderingObservations);
        var partitionRouting = new AssertionCheck(
            commandObservations > 0 && partitionRoutingViolations.Count == 0,
            commandObservations == 0
                ? "No payment-command observations were available"
                : partitionRoutingViolations.Count == 0
                    ? $"Every one of {commandObservations} payment-command row(s) sits in its debtor account's partition"
                    : $"{partitionRoutingViolations.Count} misrouted row(s); first: {partitionRoutingViolations[0].Store} {partitionRoutingViolations[0].IdempotencyKey} from {partitionRoutingViolations[0].FromAccount} in partition {partitionRoutingViolations[0].PartitionId}, expected {partitionRoutingViolations[0].ExpectedPartitionId}");
        var inlineInstantSettlement = new AssertionCheck(
            request.InlineInstantSettlementCount > 0,
            $"Fresh instant payments completed inline: {request.InlineInstantSettlementCount}");

        // Stage cardinality with cancellations (spec matrix): the outbox holds
        // exactly N rows, every one of them Completed or Cancelled; CoreBank's
        // inbox holds one Completed row per completed outbox row plus, at
        // most, one Cancelled row (tombstone or cancelled command) per
        // cancelled outbox row -- never a Completed row for a cancelled
        // payment, which would be a payment executed after being cancelled;
        // and the event stores carry exactly three events per COMPLETED
        // payment plus one transaction.cancelled event per CoreBank-side
        // cancellation (spec: instant-rail-cancelled-event -- the event is
        // enqueued atomically with the cancel, so an inbox Cancelled row
        // without its event, or an event without its row, is a defect). A
        // local cancel (no inbox row) publishes nothing. With no cancellations
        // this is the original N/N/3N/3N gate unchanged.
        var completedCount = paymentsOutbox.Completed;
        var expectedEvents = completedCount * 3 + coreBankInbox.Cancelled;
        var cardinalityPassed = !expectedUnique.HasValue ||
            (paymentsOutbox.Total == expectedUnique.Value
             && paymentsOutbox.Completed + paymentsOutbox.Cancelled == expectedUnique.Value
             && coreBankInbox.Completed == completedCount
             && coreBankInbox.Cancelled <= paymentsOutbox.Cancelled
             && coreBankInbox.Total == coreBankInbox.Completed + coreBankInbox.Cancelled
             && coreBankOutbox.Total == expectedEvents
             && coreBankOutbox.Completed == expectedEvents
             && paymentsInbox.Total == expectedEvents
             && paymentsInbox.Completed == expectedEvents);
        var stageCardinality = new StageCardinalityCheck(
            cardinalityPassed,
            expectedUnique.HasValue
                // The headline keeps its pre-cancellation shape verbatim whenever
                // no CoreBank-side cancellation exists; with one, the event figures
                // print the number the decision actually uses.
                ? (coreBankInbox.Cancelled == 0
                      ? $"Expected N/N/3N/3N={expectedUnique.Value}/{expectedUnique.Value}/{expectedUnique.Value * 3}/{expectedUnique.Value * 3}"
                      : $"Expected N/N/(3xCompleted+Cancelled)/(3xCompleted+Cancelled)={expectedUnique.Value}/{expectedUnique.Value}/{expectedEvents}/{expectedEvents}")
                  + $"; Actual={paymentsOutbox.Total}/{coreBankInbox.Total}/{coreBankOutbox.Total}/{paymentsInbox.Total}"
                  + (cancelledDetail.Length > 0
                      ? $" (cancelled instant rows are terminal: Completed+Cancelled=N, downstream 3x{completedCount}+{coreBankInbox.Cancelled} cancelled event(s)={expectedEvents}{cancelledDetail})"
                      : string.Empty)
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
            perAccountOrdering,
            partitionRouting,
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
            perAccountOrdering.Passed &&
            partitionRouting.Passed &&
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
            new AssertionDebugInfo(
                completedTransactions, orderingViolations, accountOrderingViolations, partitionRoutingViolations));
    }

    /// <summary>
    /// FIFO per <c>(Store, PartitionId, Priority)</c>: the lane a processor
    /// actually works. FIFO is a promise within a priority class, not across
    /// them: the instant rail is *meant* to overtake queued standard work in
    /// the same partition (an SCT Inst never waits behind batch SCT work), so
    /// rows are only ever compared against rows of their own priority.
    /// </summary>
    internal static IReadOnlyList<OrderingViolation> FindOrderingViolations(
        IReadOnlyList<OrderingObservation> observations) =>
        ExecutedRows(observations)
            .GroupBy(item => (item.Store, item.PartitionId, item.Priority))
            .SelectMany(lane => FindInversions(lane).Select(pair => new OrderingViolation(
                lane.Key.Store,
                lane.Key.PartitionId,
                pair.Earlier.IdempotencyKey,
                pair.Later.IdempotencyKey,
                pair.Earlier.EnqueuedAt,
                pair.Later.EnqueuedAt,
                pair.Earlier.ProcessedAt!.Value,
                pair.Later.ProcessedAt!.Value)))
            .ToList();

    /// <summary>
    /// FIFO per <c>(Store, FromAccount, Priority)</c> over the payment-command
    /// rows (ADR-026): the guarantee the demo makes, independent of which
    /// partition each row sits in. Same priority-class and Cancelled rules
    /// as <see cref="FindOrderingViolations"/>.
    /// </summary>
    internal static IReadOnlyList<AccountOrderingViolation> FindAccountOrderingViolations(
        IReadOnlyList<OrderingObservation> observations) =>
        ExecutedRows(observations)
            .Where(item => item.FromAccount is not null)
            .GroupBy(item => (item.Store, FromAccount: item.FromAccount!, item.Priority))
            .SelectMany(lane => FindInversions(lane).Select(pair => new AccountOrderingViolation(
                lane.Key.Store,
                lane.Key.FromAccount,
                lane.Key.Priority,
                pair.Earlier.IdempotencyKey,
                pair.Later.IdempotencyKey,
                pair.Earlier.EnqueuedAt,
                pair.Later.EnqueuedAt,
                pair.Earlier.ProcessedAt!.Value,
                pair.Later.ProcessedAt!.Value)))
            .ToList();

    /// <summary>
    /// Every payment-command row, Cancelled tombstones included, must sit in
    /// <see cref="PartitionHelper"/>'s partition for its debtor account as
    /// stored (ADR-026). A tombstone in another lane would not shield the
    /// late original from executing out of order, so none is exempt.
    /// </summary>
    internal static IReadOnlyList<PartitionRoutingViolation> FindPartitionRoutingViolations(
        IReadOnlyList<OrderingObservation> observations) =>
        observations
            .Where(item => item.FromAccount is not null)
            .Select(item => (Row: item, Expected: PartitionHelper.GetPartitionId(item.FromAccount!, LoadTestConstants.PartitionCount)))
            .Where(candidate => candidate.Row.PartitionId != candidate.Expected)
            .Select(candidate => new PartitionRoutingViolation(
                candidate.Row.Store,
                candidate.Row.IdempotencyKey,
                candidate.Row.FromAccount!,
                candidate.Row.PartitionId,
                candidate.Expected))
            .ToList();

    /// <summary>
    /// A Cancelled row is left out entirely (spec: instant-rail-timeout-
    /// cancel): it never executed, so it is not a step in execution order.
    /// Its ProcessedAt is when it was withdrawn, which can legally precede
    /// the completion of an older row it was waiting behind -- an older
    /// instant row that held the lock through its forward phase and cancel,
    /// was released on the residual unknown and completed later by the
    /// background rail, while the younger row behind it hit its own forward
    /// deadline first and was cancelled locally. A CoreBank tombstone
    /// (ReceivedAt == ProcessedAt) versus an older still-pending row is the
    /// same shape.
    /// </summary>
    private static IEnumerable<OrderingObservation> ExecutedRows(IEnumerable<OrderingObservation> observations) =>
        observations.Where(item => item.Status != Status.Cancelled);

    /// <summary>
    /// Walks one FIFO lane in enqueue order and yields, for every row, the
    /// latest-processed row that was owed precedence over it
    /// (<see cref="MustPrecede"/>) yet was processed after it. Both rows of
    /// a pair carry a ProcessedAt.
    /// </summary>
    private static IEnumerable<(OrderingObservation Earlier, OrderingObservation Later)> FindInversions(
        IEnumerable<OrderingObservation> lane)
    {
        var rows = lane
            .Where(item => item.ProcessedAt.HasValue)
            .OrderBy(item => item.EnqueuedAt)
            .ThenBy(item => item.Id)
            .ToList();
        foreach (var later in rows)
        {
            var earlier = rows
                .Where(candidate => !ReferenceEquals(candidate, later)
                    && MustPrecede(candidate, later)
                    && candidate.ProcessedAt > later.ProcessedAt)
                .OrderByDescending(candidate => candidate.ProcessedAt)
                .FirstOrDefault();
            if (earlier is not null)
            {
                yield return (earlier, later);
            }
        }
    }

    /// <summary>
    /// Whether the lane owes <paramref name="earlier"/> precedence over
    /// <paramref name="later"/>. With a commit timestamp
    /// (<see cref="OrderingObservation.AcceptedAt"/>) that is "accepted before
    /// the other was stamped": two requests in flight at the same time have
    /// no order the lane could have honoured, because it only sees committed
    /// rows and orders those by stamp. Without one, stamp order is binding,
    /// with Id as the same tiebreaker <c>GetClaimableMessagesQuery</c> uses for
    /// rows sharing a stamp (timestamps come from TimeProvider.GetUtcNow()
    /// per request, so ties are realistic under load).
    /// </summary>
    private static bool MustPrecede(OrderingObservation earlier, OrderingObservation later) =>
        earlier.AcceptedAt is { } accepted
            ? accepted < later.EnqueuedAt
            : earlier.EnqueuedAt < later.EnqueuedAt
              || (earlier.EnqueuedAt == later.EnqueuedAt && earlier.Id.CompareTo(later.Id) < 0);

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
