using AwesomeAssertions;
using CoreBankDemo.LoadTestSupport.Services;
using Xunit;

namespace CoreBankDemo.LoadTestSupport.Tests;

/// <summary>
/// Docker-free unit tests for <see cref="LoadTestAssertionCalculator.ComputeAssertionResult"/>
/// and <see cref="LoadTestAssertionCalculator.CalculateExpectedBalances"/> — the pure
/// five-invariant/balance-replay math extracted from the former duplicated
/// REST/MCP implementations. No DbContext, no PostgreSQL: every input is
/// already-fetched, in-memory data, matching the split established by
/// <see cref="DatabaseResetCoordinatorTests"/> (pure logic here; EF-backed
/// queries covered separately by the persistence integration tier).
/// </summary>
public class LoadTestAssertionServiceTests
{
    private const decimal InitialBalance = LoadTestConstants.InitialBalance;
    private const int AccountCount = LoadTestConstants.AccountCount;

    private static string AccountNumber(int i) => $"NL{i:D2}LOAD{i:D10}";

    private static List<LoadTestAccountBalance> UntouchedAccounts() =>
        Enumerable.Range(1, AccountCount)
            .Select(i => new LoadTestAccountBalance(AccountNumber(i), InitialBalance))
            .ToList();

    private static AssertionResult Compute(
        int? expectedUnique = null,
        int completedCount = 0,
        int failedCount = 0,
        int pendingCount = 0,
        IReadOnlyList<CompletedTransaction>? completedTransactions = null,
        IReadOnlyList<DuplicateKeyInfo>? duplicateKeys = null,
        int totalOutbox = 0,
        int outboxCompleted = 0,
        int outboxPending = 0,
        int outboxUniqueKeys = 0,
        IReadOnlyList<LoadTestAccountBalance>? loadTestAccounts = null,
        IReadOnlyList<OrderingObservation>? orderingObservations = null,
        int inlineSettlementCount = 1)
    {
        var downstreamCount = (expectedUnique ?? 0) * 3;
        var transactions = completedTransactions ?? [];
        var ordering = orderingObservations ?? transactions
            .Select((transaction, index) => new OrderingObservation(
                "CoreBankInbox",
                0,
                transaction.IdempotencyKey,
                new DateTime(2026, 1, 1).AddSeconds(index),
                new DateTime(2026, 1, 1).AddSeconds(index + 1)))
            .ToList();
        return
        LoadTestAssertionCalculator.ComputeAssertionResult(new ComputeAssertionRequest(
            ExpectedUnique: expectedUnique,
            PaymentsOutbox: new MessageStoreSummary(totalOutbox, outboxCompleted, 0, outboxPending),
            CoreBankInbox: new MessageStoreSummary(completedCount + failedCount + pendingCount, completedCount, failedCount, pendingCount),
            CoreBankOutbox: new MessageStoreSummary(downstreamCount, downstreamCount, 0, 0),
            PaymentsInbox: new MessageStoreSummary(downstreamCount, downstreamCount, 0, 0),
            CompletedTransactions: transactions,
            DuplicateKeys: duplicateKeys ?? [],
            OutboxUniqueKeys: outboxUniqueKeys,
            LoadTestAccounts: loadTestAccounts ?? UntouchedAccounts(),
            OrderingObservations: ordering,
            InlineInstantSettlementCount: inlineSettlementCount));
    }

    [Fact]
    public void Untouched_accounts_do_not_claim_ordering_proof_with_zero_activity()
    {
        var result = Compute(expectedUnique: 0, totalOutbox: 0, loadTestAccounts: UntouchedAccounts());

        result.AllPassed.Should().BeFalse();
        result.Checks.NoFailedMessages.Passed.Should().BeTrue();
        result.Checks.NoPendingMessages.Passed.Should().BeTrue();
        result.Checks.NoDuplicateProcessing.Passed.Should().BeTrue();
        result.Checks.ExpectedUniqueProcessed.Passed.Should().BeTrue();
        result.Checks.AllSubmittedProcessed.Passed.Should().BeTrue();
        result.Checks.BalanceConservation.Passed.Should().BeTrue();
        result.Checks.BalancesCorrect.Passed.Should().BeTrue();
        result.Checks.PerKeyOrdering.Passed.Should().BeFalse();
        result.Summary.TotalBalance.Should().Be(AccountCount * InitialBalance);
    }

    [Fact]
    public void Duplicate_idempotency_key_replay_fails_dedupe_but_distinct_count_matches_expected()
    {
        // Seeded data: N=2 unique completed transfers, one of which also has a
        // second (duplicate-key) inbox row recorded — exactly-once dedupe
        // must flag the duplicate while still reporting 2 distinct processed.
        var transactions = new List<CompletedTransaction>
        {
            new(AccountNumber(1), AccountNumber(2), 100m, "key-1"),
            new(AccountNumber(3), AccountNumber(4), 50m, "key-2")
        };
        var duplicates = new List<DuplicateKeyInfo> { new("key-1", 2) };
        var accounts = UntouchedAccounts();
        accounts[0] = accounts[0] with { Balance = InitialBalance - 100m };
        accounts[1] = accounts[1] with { Balance = InitialBalance + 100m };
        accounts[2] = accounts[2] with { Balance = InitialBalance - 50m };
        accounts[3] = accounts[3] with { Balance = InitialBalance + 50m };

        var result = Compute(
            expectedUnique: 2,
            completedCount: 2,
            completedTransactions: transactions,
            duplicateKeys: duplicates,
            totalOutbox: 2,
            loadTestAccounts: accounts);

        result.Checks.NoDuplicateProcessing.Passed.Should().BeFalse();
        result.Checks.NoDuplicateProcessing.Duplicates.Should().ContainSingle()
            .Which.Should().Be(new DuplicateKeyInfo("key-1", 2));
        result.Checks.ExpectedUniqueProcessed.Passed.Should().BeTrue("distinct completed keys still equal expectedUnique");
        result.Summary.CompletedUniqueKeys.Should().Be(2);
        result.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void Not_all_submitted_payments_were_processed_fails_all_submitted_processed()
    {
        var result = Compute(expectedUnique: 5, completedCount: 4, totalOutbox: 5);

        result.Checks.AllSubmittedProcessed.Passed.Should().BeFalse();
        result.Checks.AllSubmittedProcessed.Detail.Should().Contain("OutboxTotal=5").And.Contain("InboxCompleted=4");
        result.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void Balance_conservation_fails_when_total_balance_drifts_from_the_constant_sum()
    {
        var accounts = UntouchedAccounts();
        accounts[0] = accounts[0] with { Balance = accounts[0].Balance - 1m }; // leaked a unit somewhere

        var result = Compute(expectedUnique: 0, loadTestAccounts: accounts);

        result.Checks.BalanceConservation.Passed.Should().BeFalse();
        result.Summary.TotalBalance.Should().Be(AccountCount * InitialBalance - 1m);
        result.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void Genuine_transport_failure_fails_no_failed_messages()
    {
        var result = Compute(expectedUnique: 0, failedCount: 1);

        result.Checks.NoFailedMessages.Passed.Should().BeFalse();
        result.Checks.NoFailedMessages.Detail.Should().Contain("1 failed");
        result.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void Business_rejection_cached_as_completed_never_counts_as_failed()
    {
        // AD-11: a business-rejected transaction that completed with a cached
        // failure payload is a Completed inbox row, not a Failed one — the
        // caller never passes it in as failedCount, so NoFailedMessages stays
        // green even though the transaction was, semantically, rejected.
        var result = Compute(expectedUnique: 1, completedCount: 1, failedCount: 0, totalOutbox: 1);

        result.Checks.NoFailedMessages.Passed.Should().BeTrue();
    }

    [Fact]
    public void Pending_messages_fail_no_pending_messages()
    {
        var result = Compute(expectedUnique: 0, pendingCount: 3);

        result.Checks.NoPendingMessages.Passed.Should().BeFalse();
        result.Checks.NoPendingMessages.Detail.Should().Contain("3 still pending/processing");
        result.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void Null_expected_unique_always_passes_the_expected_unique_check()
    {
        var transactions = new List<CompletedTransaction>
        {
            new(AccountNumber(1), AccountNumber(2), 10m, "key-1")
        };

        var result = Compute(expectedUnique: null, completedCount: 1, completedTransactions: transactions, totalOutbox: 1);

        result.Checks.ExpectedUniqueProcessed.Passed.Should().BeTrue();
        result.Checks.ExpectedUniqueProcessed.Detail.Should().NotContain("ExpectedUnique=");
    }

    [Fact]
    public void Mismatched_expected_unique_fails_the_check()
    {
        var transactions = new List<CompletedTransaction>
        {
            new(AccountNumber(1), AccountNumber(2), 10m, "key-1")
        };

        var result = Compute(expectedUnique: 2, completedCount: 1, completedTransactions: transactions, totalOutbox: 1);

        result.Checks.ExpectedUniqueProcessed.Passed.Should().BeFalse();
        result.Checks.ExpectedUniqueProcessed.Detail.Should().Be("ExpectedUnique=2, CompletedUnique=1");
    }

    [Fact]
    public void Balances_correct_by_replay_flags_an_account_whose_persisted_balance_diverges_from_the_replay()
    {
        var transactions = new List<CompletedTransaction>
        {
            new(AccountNumber(1), AccountNumber(2), 200m, "key-1")
        };
        var accounts = UntouchedAccounts();
        // Correct replay: account 1 -200, account 2 +200. Persist a wrong value for account 2.
        accounts[0] = accounts[0] with { Balance = InitialBalance - 200m };
        accounts[1] = accounts[1] with { Balance = InitialBalance + 150m }; // should be +200

        var result = Compute(
            expectedUnique: 1,
            completedCount: 1,
            completedTransactions: transactions,
            totalOutbox: 1,
            loadTestAccounts: accounts);

        result.Checks.BalancesCorrect.Passed.Should().BeFalse();
        result.Checks.BalancesCorrect.Discrepancies.Should().ContainSingle();
        var discrepancy = result.Checks.BalancesCorrect.Discrepancies[0];
        discrepancy.AccountNumber.Should().Be(AccountNumber(2));
        discrepancy.Expected.Should().Be(InitialBalance + 200m);
        discrepancy.Actual.Should().Be(InitialBalance + 150m);
        discrepancy.Difference.Should().Be(-50m);
        result.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void Balances_correct_by_replay_passes_when_persisted_balances_match_the_replayed_expectation()
    {
        var transactions = new List<CompletedTransaction>
        {
            new(AccountNumber(1), AccountNumber(2), 200m, "key-1"),
            new(AccountNumber(2), AccountNumber(3), 75m, "key-2")
        };
        var accounts = UntouchedAccounts();
        accounts[0] = accounts[0] with { Balance = InitialBalance - 200m };
        accounts[1] = accounts[1] with { Balance = InitialBalance + 200m - 75m };
        accounts[2] = accounts[2] with { Balance = InitialBalance + 75m };

        var result = Compute(
            expectedUnique: 2,
            completedCount: 2,
            completedTransactions: transactions,
            totalOutbox: 2,
            loadTestAccounts: accounts);

        result.Checks.BalancesCorrect.Passed.Should().BeTrue();
        result.Checks.BalancesCorrect.Discrepancies.Should().BeEmpty();
        result.Summary.TotalBalance.Should().Be(AccountCount * InitialBalance);
        result.Checks.BalanceConservation.Passed.Should().BeTrue();
    }

    [Fact]
    public void Balances_correct_skips_an_account_that_is_not_a_seeded_load_test_account()
    {
        // Branch coverage for the discrepancy loop's `TryGetValue` miss path:
        // an account outside the NL{i:D2}LOAD{i:D10} seeded set (i beyond
        // AccountCount) has no entry in CalculateExpectedBalances' dictionary,
        // so it must be silently skipped rather than flagged as a discrepancy.
        var accounts = UntouchedAccounts();
        accounts.Add(new LoadTestAccountBalance(AccountNumber(AccountCount + 1), 999m));

        var result = Compute(expectedUnique: 0, loadTestAccounts: accounts);

        result.Checks.BalancesCorrect.Passed.Should().BeTrue("the out-of-range account has no expected balance to compare against");
        result.Checks.BalancesCorrect.Discrepancies.Should().BeEmpty();
        result.Summary.AccountCount.Should().Be(AccountCount + 1);
    }

    [Fact]
    public void CalculateExpectedBalances_ignores_a_transaction_referencing_an_unknown_account()
    {
        var transactions = new List<CompletedTransaction>
        {
            new("NOT-A-LOAD-ACCOUNT", AccountNumber(1), 500m, "key-1")
        };

        var balances = LoadTestAssertionCalculator.CalculateExpectedBalances(transactions);

        balances.Should().HaveCount(AccountCount);
        balances[AccountNumber(1)].Should().Be(InitialBalance, "the unknown-account leg means neither side of the transfer applies");
    }

    [Fact]
    public void CalculateExpectedBalances_seeds_every_load_test_account_at_the_initial_balance()
    {
        var balances = LoadTestAssertionCalculator.CalculateExpectedBalances([]);

        balances.Should().HaveCount(AccountCount);
        balances.Values.Should().OnlyContain(b => b == InitialBalance);
    }

    [Fact]
    public void Debug_carries_through_the_completed_transactions_used_for_the_replay()
    {
        var transactions = new List<CompletedTransaction>
        {
            new(AccountNumber(1), AccountNumber(2), 10m, "key-1")
        };

        var result = Compute(expectedUnique: 1, completedCount: 1, completedTransactions: transactions, totalOutbox: 1);

        result.Debug.CompletedTransactions.Should().BeEquivalentTo(transactions);
    }

    [Fact]
    public void Failed_row_in_any_store_fails_the_terminal_state_gate()
    {
        var result = LoadTestAssertionCalculator.ComputeAssertionResult(new ComputeAssertionRequest(
            ExpectedUnique: 0,
            PaymentsOutbox: new MessageStoreSummary(0, 0, 0, 0),
            CoreBankInbox: new MessageStoreSummary(0, 0, 0, 0),
            CoreBankOutbox: new MessageStoreSummary(1, 0, 1, 0),
            PaymentsInbox: new MessageStoreSummary(0, 0, 0, 0),
            CompletedTransactions: [],
            DuplicateKeys: [],
            OutboxUniqueKeys: 0,
            LoadTestAccounts: UntouchedAccounts()));

        result.Checks.NoFailedMessages.Passed.Should().BeFalse();
        result.Checks.NoFailedMessages.Detail.Should().Contain("CoreBankOutbox=1");
        result.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void Green_stage_counts_and_exact_accounts_pass_new_acceptance_checks()
    {
        var result = LoadTestAssertionCalculator.ComputeAssertionResult(new ComputeAssertionRequest(
            ExpectedUnique: 2,
            PaymentsOutbox: new MessageStoreSummary(2, 2, 0, 0),
            CoreBankInbox: new MessageStoreSummary(2, 2, 0, 0),
            CoreBankOutbox: new MessageStoreSummary(6, 6, 0, 0),
            PaymentsInbox: new MessageStoreSummary(6, 6, 0, 0),
            CompletedTransactions:
            [
                new CompletedTransaction(AccountNumber(1), AccountNumber(2), 1m, "key-1"),
                new CompletedTransaction(AccountNumber(2), AccountNumber(1), 1m, "key-2")
            ],
            DuplicateKeys: [],
            OutboxUniqueKeys: 2,
            LoadTestAccounts: UntouchedAccounts(),
            OrderingObservations:
            [
                new OrderingObservation("CoreBankInbox", 0, "key-1", new DateTime(2026, 1, 1), new DateTime(2026, 1, 1, 0, 0, 1)),
                new OrderingObservation("CoreBankInbox", 0, "key-2", new DateTime(2026, 1, 1, 0, 0, 2), new DateTime(2026, 1, 1, 0, 0, 3)),
            ],
            InlineInstantSettlementCount: 1));

        result.Checks.StageCardinality.Passed.Should().BeTrue();
        result.Checks.CanonicalAccountSet.Passed.Should().BeTrue();
        result.AllPassed.Should().BeTrue();
    }

    [Fact]
    public void Ordering_inversion_fails_with_exact_partition_evidence()
    {
        var observations = new[]
        {
            new OrderingObservation("PaymentsOutbox", 2, "earlier", new DateTime(2026, 1, 1, 0, 0, 0), new DateTime(2026, 1, 1, 0, 0, 5)),
            new OrderingObservation("PaymentsOutbox", 2, "later", new DateTime(2026, 1, 1, 0, 0, 1), new DateTime(2026, 1, 1, 0, 0, 4)),
        };

        var result = Compute(expectedUnique: 0, orderingObservations: observations);

        result.Checks.PerKeyOrdering.Passed.Should().BeFalse();
        result.Checks.PerKeyOrdering.Detail.Should().Contain("PaymentsOutbox").And.Contain("partition-2");
        result.Debug.OrderingViolations.Should().ContainSingle();
    }

    [Fact]
    public void Ordering_inversion_is_still_detected_when_both_rows_share_an_identical_enqueued_timestamp()
    {
        // Patch 5 regression test: timestamps come from TimeProvider.GetUtcNow()
        // per-request, so two rows in the same partition can genuinely land on
        // the exact same tick under real concurrent load. Grouping solely by
        // EnqueuedAt used to collapse them into one incomparable group; the Id
        // tiebreaker (the same column GetClaimableMessagesQuery already uses)
        // must keep them distinguishable so a real violation is still caught.
        var sameInstant = new DateTime(2026, 1, 1, 0, 0, 0);
        var earlierId = new Guid("00000000-0000-0000-0000-000000000001");
        var laterId = new Guid("00000000-0000-0000-0000-000000000002");
        var observations = new[]
        {
            new OrderingObservation(
                "PaymentsOutbox", 2, "earlier", sameInstant, new DateTime(2026, 1, 1, 0, 0, 5), earlierId),
            new OrderingObservation(
                "PaymentsOutbox", 2, "later", sameInstant, new DateTime(2026, 1, 1, 0, 0, 4), laterId),
        };

        var result = Compute(expectedUnique: 0, orderingObservations: observations);

        result.Checks.PerKeyOrdering.Passed.Should().BeFalse();
        result.Checks.PerKeyOrdering.Detail.Should().Contain("PaymentsOutbox").And.Contain("partition-2");
        result.Debug.OrderingViolations.Should().ContainSingle();
    }

    [Fact]
    public void Inline_settlement_requires_authoritative_positive_count()
    {
        var result = Compute(expectedUnique: 0, inlineSettlementCount: 0);

        result.Checks.InlineInstantSettlement.Passed.Should().BeFalse();
        result.Summary.InlineInstantSettlementCount.Should().Be(0);
    }

    [Fact]
    public void Missing_or_unexpected_load_account_fails_exact_account_check()
    {
        var accounts = UntouchedAccounts();
        accounts.RemoveAt(0);
        accounts.Add(new LoadTestAccountBalance("NL99LOAD0000000099", InitialBalance));

        var result = Compute(expectedUnique: 0, loadTestAccounts: accounts);

        result.Checks.CanonicalAccountSet.Passed.Should().BeFalse();
        result.Checks.CanonicalAccountSet.Missing.Should().Contain(AccountNumber(1));
        result.Checks.CanonicalAccountSet.Unexpected.Should().Contain("NL99LOAD0000000099");
    }
}
