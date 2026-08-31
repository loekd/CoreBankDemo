using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.LoadTestSupport;
using CoreBankDemo.LoadTestSupport.Services;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.LoadTestSupport;

/// <summary>
/// EF-backed integration coverage for <see cref="LoadTestAssertionService"/>
/// — story 7.1's realignment. Exercises <see cref="LoadTestAssertionService.CheckDrainAsync"/>
/// against real PostgreSQL with rows planted in each of the four message
/// stores individually (the pre-story-7.1 bug this story fixes: drain only
/// polled 2 of the 4), and <see cref="LoadTestAssertionService.GetResultsAsync"/>
/// against seeded inbox/outbox/account data. Follows
/// <see cref="LoadTestSupport.LoadTestDatabaseResetterTests"/>'s dual-context,
/// per-test isolated database pattern (constructs the service directly rather
/// than through HTTP, matching that precedent).
/// </summary>
public sealed class AssertEndpointsIntegrationTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private string? _coreBankConnectionString;
    private string? _paymentsConnectionString;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _coreBankConnectionString = await fixture.CreateDatabaseAsync("assertcore", cancellationToken);
        _paymentsConnectionString = await fixture.CreateDatabaseAsync("assertpayments", cancellationToken);

        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        await coreBank.Database.EnsureCreatedAsync(cancellationToken);
        await payments.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_coreBankConnectionString is not null)
        {
            await fixture.DropDatabaseAsync(_coreBankConnectionString);
        }

        if (_paymentsConnectionString is not null)
        {
            await fixture.DropDatabaseAsync(_paymentsConnectionString);
        }
    }

    [Fact]
    public async Task Drain_reports_not_drained_when_only_corebank_outbox_has_a_pending_row()
    {
        // This is the exact gap story 7.1 closes: coreBankDb.MessagingOutboxMessages
        // was never polled before, so this single Pending row would previously
        // have been invisible to /assert/drain and poll_until_drained.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        coreBank.MessagingOutboxMessages.Add(CoreBankOutbox("outbox-1", MessageConstants.Status.Pending));
        await coreBank.SaveChangesAsync(cancellationToken);

        var service = new LoadTestAssertionService(coreBank, payments);
        var result = await service.CheckDrainAsync(cancellationToken);

        result.IsDrained.Should().BeFalse();
        result.CoreBankOutboxPending.Should().Be(1);
        result.OutboxPending.Should().Be(0);
        result.InboxPending.Should().Be(0);
        result.PaymentsInboxPending.Should().Be(0);
    }

    [Fact]
    public async Task Drain_reports_not_drained_when_only_payments_inbox_has_a_processing_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        var inbox = PaymentsApiTestData.Inbox("payments-inbox-1", "BalanceUpdated", "account-1");
        inbox.Status = MessageConstants.Status.Processing;
        payments.InboxMessages.Add(inbox);
        await payments.SaveChangesAsync(cancellationToken);

        var service = new LoadTestAssertionService(coreBank, payments);
        var result = await service.CheckDrainAsync(cancellationToken);

        result.IsDrained.Should().BeFalse();
        result.PaymentsInboxPending.Should().Be(1);
        result.OutboxPending.Should().Be(0);
        result.InboxPending.Should().Be(0);
        result.CoreBankOutboxPending.Should().Be(0);
    }

    [Fact]
    public async Task Drain_reports_not_drained_when_payments_outbox_has_a_pending_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        payments.OutboxMessages.Add(PaymentsApiTestData.Outbox("payments-outbox-1"));
        await payments.SaveChangesAsync(cancellationToken);

        var service = new LoadTestAssertionService(coreBank, payments);
        var result = await service.CheckDrainAsync(cancellationToken);

        result.IsDrained.Should().BeFalse();
        result.OutboxPending.Should().Be(1);
    }

    [Fact]
    public async Task Drain_reports_not_drained_when_corebank_inbox_has_a_pending_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        coreBank.InboxMessages.Add(CoreBankInbox("core-inbox-1", MessageConstants.Status.Pending));
        await coreBank.SaveChangesAsync(cancellationToken);

        var service = new LoadTestAssertionService(coreBank, payments);
        var result = await service.CheckDrainAsync(cancellationToken);

        result.IsDrained.Should().BeFalse();
        result.InboxPending.Should().Be(1);
    }

    [Fact]
    public async Task Drain_reports_drained_when_all_four_stores_are_terminal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        coreBank.InboxMessages.Add(CoreBankInbox("core-inbox-done", MessageConstants.Status.Completed));
        coreBank.MessagingOutboxMessages.Add(CoreBankOutbox("core-outbox-done", MessageConstants.Status.Completed));
        var doneOutbox = PaymentsApiTestData.Outbox("payments-outbox-done");
        doneOutbox.Status = MessageConstants.Status.Completed;
        payments.OutboxMessages.Add(doneOutbox);
        var doneInbox = PaymentsApiTestData.Inbox("payments-inbox-done", "BalanceUpdated", "account-done");
        doneInbox.Status = MessageConstants.Status.Completed;
        payments.InboxMessages.Add(doneInbox);
        await coreBank.SaveChangesAsync(cancellationToken);
        await payments.SaveChangesAsync(cancellationToken);

        var service = new LoadTestAssertionService(coreBank, payments);
        var result = await service.CheckDrainAsync(cancellationToken);

        result.IsDrained.Should().BeTrue();
        result.OutboxPending.Should().Be(0);
        result.InboxPending.Should().Be(0);
        result.CoreBankOutboxPending.Should().Be(0);
        result.PaymentsInboxPending.Should().Be(0);
        result.Completed.Should().Be(1);
        result.Failed.Should().Be(0);
    }

    [Fact]
    public async Task Results_reports_no_duplicates_and_matching_distinct_count_for_n_unique_completed_transactions()
    {
        // coreBankDb.InboxMessages.IdempotencyKey carries a unique index (AD-4
        // dedupe), so a literal duplicate row can never exist here — this
        // proves the N-unique side of the I/O matrix's duplicate-replay row:
        // NoDuplicateProcessing stays true and the distinct count equals N.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        SeedLoadAccounts(coreBank, 1, 2);
        coreBank.InboxMessages.Add(CompletedTransfer("key-1", 1, 2, 10m));
        coreBank.InboxMessages.Add(CompletedTransfer("key-2", 2, 1, 5m));
        payments.OutboxMessages.Add(CompletedOutbox("key-1"));
        payments.OutboxMessages.Add(CompletedOutbox("key-2"));
        await coreBank.SaveChangesAsync(cancellationToken);
        await payments.SaveChangesAsync(cancellationToken);

        var service = new LoadTestAssertionService(coreBank, payments);
        var result = await service.GetResultsAsync(expectedUnique: 2, cancellationToken);

        result.Checks.NoDuplicateProcessing.Passed.Should().BeTrue();
        result.Checks.NoDuplicateProcessing.Duplicates.Should().BeEmpty();
        result.Checks.ExpectedUniqueProcessed.Passed.Should().BeTrue();
        result.Summary.CompletedUniqueKeys.Should().Be(2);
    }

    [Fact]
    public async Task Results_fails_no_failed_messages_for_a_genuine_transport_failure_after_retries_exhausted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        var failed = CoreBankInbox("core-inbox-failed", MessageConstants.Status.Failed);
        failed.RetryCount = MessageConstants.Defaults.MaxRetryCount;
        failed.LastError = "transport exhausted";
        coreBank.InboxMessages.Add(failed);
        await coreBank.SaveChangesAsync(cancellationToken);

        var service = new LoadTestAssertionService(coreBank, payments);
        var result = await service.GetResultsAsync(expectedUnique: null, cancellationToken);

        result.Checks.NoFailedMessages.Passed.Should().BeFalse();
        result.AllPassed.Should().BeFalse();
    }

    [Fact]
    public async Task Results_never_counts_a_business_rejected_but_completed_row_as_failed()
    {
        // AD-11: a business rejection is a successfully processed message
        // (Completed, with a cached failure ResponsePayload) — never Failed.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        var rejected = CoreBankInbox("core-inbox-rejected", MessageConstants.Status.Completed);
        rejected.ResponsePayload = "{\"status\":\"Rejected\",\"reason\":\"insufficient funds\"}";
        coreBank.InboxMessages.Add(rejected);
        await coreBank.SaveChangesAsync(cancellationToken);

        var service = new LoadTestAssertionService(coreBank, payments);
        var result = await service.GetResultsAsync(expectedUnique: null, cancellationToken);

        result.Checks.NoFailedMessages.Passed.Should().BeTrue();
        result.Summary.InboxFailed.Should().Be(0);
        result.Summary.InboxCompleted.Should().Be(1);
    }

    [Fact]
    public async Task Results_flags_a_balance_discrepancy_when_persisted_balance_diverges_from_the_transaction_replay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        var accounts = SeedLoadAccounts(coreBank, 1, 2);
        // Replay expects account 2 at InitialBalance + 100; persist a wrong value.
        accounts[1].Balance = LoadTestConstants.InitialBalance + 40m;
        coreBank.InboxMessages.Add(CompletedTransfer("key-1", 1, 2, 100m));
        payments.OutboxMessages.Add(CompletedOutbox("key-1"));
        await coreBank.SaveChangesAsync(cancellationToken);
        await payments.SaveChangesAsync(cancellationToken);

        var service = new LoadTestAssertionService(coreBank, payments);
        var result = await service.GetResultsAsync(expectedUnique: 1, cancellationToken);

        result.Checks.BalancesCorrect.Passed.Should().BeFalse();
        var accountTwoNumber = AccountNumber(2);
        result.Checks.BalancesCorrect.Discrepancies.Should().ContainSingle(d => d.AccountNumber == accountTwoNumber);
        var discrepancy = result.Checks.BalancesCorrect.Discrepancies.Single(d => d.AccountNumber == accountTwoNumber);
        discrepancy.Expected.Should().Be(LoadTestConstants.InitialBalance + 100m);
        discrepancy.Actual.Should().Be(LoadTestConstants.InitialBalance + 40m);
        result.AllPassed.Should().BeFalse();
    }

    [Fact]
    public async Task Results_excludes_non_load_test_demo_accounts_from_the_balance_query()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        SeedLoadAccounts(coreBank, 1);
        coreBank.Accounts.Add(new Account
        {
            AccountNumber = "NL91ABNA0417164300", // CoreBankAPI-seeded demo account — not a LOAD account
            AccountHolderName = "Demo Account",
            Balance = 500m,
            Currency = "EUR",
            IsActive = true,
            CreatedAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc)
        });
        await coreBank.SaveChangesAsync(cancellationToken);

        var service = new LoadTestAssertionService(coreBank, payments);
        var result = await service.GetResultsAsync(expectedUnique: null, cancellationToken);

        result.Summary.AccountCount.Should().Be(1);
        result.Summary.TotalBalance.Should().Be(LoadTestConstants.InitialBalance);
    }

    [Fact]
    public async Task Results_reports_green_four_store_cardinality_and_exact_account_set()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        SeedLoadAccounts(coreBank, Enumerable.Range(1, LoadTestConstants.AccountCount).ToArray());
        coreBank.InboxMessages.Add(CompletedTransfer("key-1", 1, 2, 0m));
        payments.OutboxMessages.Add(CompletedOutbox("key-1"));
        for (var index = 0; index < 3; index++)
        {
            coreBank.MessagingOutboxMessages.Add(CoreBankOutbox($"event-{index}", MessageConstants.Status.Completed));
            var paymentsInbox = PaymentsApiTestData.Inbox($"event-{index}", "BalanceUpdated", $"account-{index}");
            paymentsInbox.Status = MessageConstants.Status.Completed;
            payments.InboxMessages.Add(paymentsInbox);
        }

        await coreBank.SaveChangesAsync(cancellationToken);
        await payments.SaveChangesAsync(cancellationToken);

        var result = await new LoadTestAssertionService(coreBank, payments).GetResultsAsync(1, cancellationToken);

        result.Checks.StageCardinality.Passed.Should().BeTrue();
        result.Checks.CanonicalAccountSet.Passed.Should().BeTrue();
        result.Summary.PaymentsOutbox.Should().Be(new MessageStoreSummary(1, 1, 0, 0));
        result.Summary.CoreBankInbox.Should().Be(new MessageStoreSummary(1, 1, 0, 0));
        result.Summary.CoreBankOutbox.Should().Be(new MessageStoreSummary(3, 3, 0, 0));
        result.Summary.PaymentsInbox.Should().Be(new MessageStoreSummary(3, 3, 0, 0));
        result.AllPassed.Should().BeTrue();
    }

    private static string AccountNumber(int i) => $"NL{i:D2}LOAD{i:D10}";

    private static List<Account> SeedLoadAccounts(CoreBankDbContext coreBank, params int[] accountIndexes)
    {
        var accounts = accountIndexes
            .Select(i => new Account
            {
                AccountNumber = AccountNumber(i),
                AccountHolderName = $"Load Test Account {i:D2}",
                Balance = LoadTestConstants.InitialBalance,
                Currency = "EUR",
                IsActive = true,
                CreatedAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc)
            })
            .ToList();
        coreBank.Accounts.AddRange(accounts);
        return accounts;
    }

    private static InboxMessage CompletedTransfer(string key, int fromIndex, int toIndex, decimal amount) =>
        new()
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = key,
            TransactionId = key,
            PartitionId = 0,
            Status = MessageConstants.Status.Completed,
            ReceivedAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
            ProcessedAt = new DateTime(2026, 8, 30, 0, 1, 0, DateTimeKind.Utc),
            FromAccount = AccountNumber(fromIndex),
            ToAccount = AccountNumber(toIndex),
            Amount = amount,
            Currency = "EUR"
        };

    private static CoreBankDemo.PaymentsAPI.Outbox.OutboxMessage CompletedOutbox(string key)
    {
        var outbox = PaymentsApiTestData.Outbox(key);
        outbox.Status = MessageConstants.Status.Completed;
        return outbox;
    }

    private static InboxMessage CoreBankInbox(string key, string status) =>
        new()
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = key,
            TransactionId = key,
            PartitionId = 0,
            Status = status,
            ReceivedAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
            FromAccount = "from",
            ToAccount = "to",
            Amount = 1m,
            Currency = "EUR"
        };

    private static MessagingOutboxMessage CoreBankOutbox(string key, string status) =>
        new()
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = key,
            PartitionId = 0,
            Status = status,
            CreatedAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
            EventOccurredAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
            TransactionId = key,
            EventType = "test.event",
            EventSource = "test",
            AccountNumber = "from",
            ToAccount = "to",
            Amount = 1m,
            Currency = "EUR",
            TransactionStatus = MessageConstants.Status.Completed
        };

    private CoreBankDbContext CreateCoreBankContext() =>
        new(new DbContextOptionsBuilder<CoreBankDbContext>()
            .UseNpgsql(_coreBankConnectionString)
            .Options);

    private PaymentsDbContext CreatePaymentsContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(_paymentsConnectionString)
            .Options);
}
