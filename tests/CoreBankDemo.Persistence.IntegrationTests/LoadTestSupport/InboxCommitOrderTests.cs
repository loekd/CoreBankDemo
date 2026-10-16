using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.LoadTestSupport.Services;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
using CoreBankInboxMessage = CoreBankDemo.CoreBankAPI.Inbox.InboxMessage;
using PaymentsInboxMessage = CoreBankDemo.PaymentsAPI.Inbox.InboxMessage;

namespace CoreBankDemo.Persistence.IntegrationTests.LoadTestSupport;

/// <summary>
/// The inbox lanes order the rows they can see by <c>ReceivedAt</c>, and they
/// can only see committed rows. Both intake handlers stamp <c>ReceivedAt</c>
/// before the insert commits, so two messages in flight at the same time
/// (the three CoreBank events of one transaction arrive concurrently on the
/// Payments side; two intakes race on the CoreBank side) may be processed in
/// either order, while a row accepted (committed) before the next was even
/// stamped must be processed first. The per-key and per-account checks read
/// the accepting transaction's <c>pg_xact_commit_timestamp</c> to tell the two
/// apart, exactly as they do for the Payments outbox
/// (<see cref="PaymentsOutboxCommitOrderTests"/>). Real PostgreSQL only.
/// </summary>
public sealed class InboxCommitOrderTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private const string DebtorAccount = "NL01LOAD0000000001";
    private const string TransactionId = "load-test-0000000216";

    private string? _coreBankConnectionString;
    private string? _paymentsConnectionString;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _coreBankConnectionString = await fixture.CreateDatabaseAsync("inboxordercore", cancellationToken);
        _paymentsConnectionString = await fixture.CreateDatabaseAsync("inboxorderpay", cancellationToken);

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_payments_inbox_events_in_flight_at_the_same_time_owe_each_other_no_order(bool stampedFirstProcessedFirst)
    {
        var ct = TestContext.Current.CancellationToken;

        // Event A: stamped first, but its accepting transaction stays open.
        await using var slow = CreatePaymentsContext();
        await using var slowTransaction = await slow.Database.BeginTransactionAsync(ct);
        var stampedFirst = PaymentsEvent("com.corebank.account.balance.updated", DateTime.UtcNow);
        slow.InboxMessages.Add(stampedFirst);
        await slow.SaveChangesAsync(ct);

        // Event B: stamped while A is still open, committed immediately.
        await using var fast = CreatePaymentsContext();
        var committedFirst = PaymentsEvent("com.corebank.transaction.completed", DateTime.UtcNow);
        fast.InboxMessages.Add(committedFirst);
        await fast.SaveChangesAsync(ct);

        // Only now does event A commit.
        await slowTransaction.CommitAsync(ct);

        var first = DateTime.UtcNow;
        var second = first.AddSeconds(1);
        committedFirst.Status = MessageConstants.Status.Completed;
        committedFirst.ProcessedAt = stampedFirstProcessedFirst ? second : first;
        await fast.SaveChangesAsync(ct);
        stampedFirst.Status = MessageConstants.Status.Completed;
        stampedFirst.ProcessedAt = stampedFirstProcessedFirst ? first : second;
        await slow.SaveChangesAsync(ct);

        var result = await GetResultsAsync(ct);

        result.Debug.OrderingViolations.Should().BeEmpty(
            "neither event was accepted before the other was stamped, so the lane owed them no order");
        result.Checks.PerKeyOrdering.Passed.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_corebank_inbox_commands_in_flight_at_the_same_time_owe_each_other_no_order(bool stampedFirstProcessedFirst)
    {
        var ct = TestContext.Current.CancellationToken;

        await using var slow = CreateCoreBankContext();
        await using var slowTransaction = await slow.Database.BeginTransactionAsync(ct);
        var stampedFirst = CoreBankCommand("stamped-first", DateTime.UtcNow);
        slow.InboxMessages.Add(stampedFirst);
        await slow.SaveChangesAsync(ct);

        await using var fast = CreateCoreBankContext();
        var committedFirst = CoreBankCommand("committed-first", DateTime.UtcNow);
        fast.InboxMessages.Add(committedFirst);
        await fast.SaveChangesAsync(ct);

        await slowTransaction.CommitAsync(ct);

        var first = DateTime.UtcNow;
        var second = first.AddSeconds(1);
        committedFirst.Status = MessageConstants.Status.Completed;
        committedFirst.ProcessedAt = stampedFirstProcessedFirst ? second : first;
        await fast.SaveChangesAsync(ct);
        stampedFirst.Status = MessageConstants.Status.Completed;
        stampedFirst.ProcessedAt = stampedFirstProcessedFirst ? first : second;
        await slow.SaveChangesAsync(ct);

        var result = await GetResultsAsync(ct);

        result.Debug.OrderingViolations.Should().BeEmpty(
            "neither command was accepted before the other was stamped, so the lane owed them no order");
        result.Checks.PerKeyOrdering.Passed.Should().BeTrue();
        result.Debug.AccountOrderingViolations.Should().BeEmpty();
        result.Checks.PerAccountOrdering.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task A_payments_inbox_event_accepted_before_the_next_was_stamped_but_processed_after_it_is_an_ordering_inversion()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var payments = CreatePaymentsContext();
        var acceptedFirst = PaymentsEvent("com.corebank.transaction.completed", DateTime.UtcNow);
        payments.InboxMessages.Add(acceptedFirst);
        await payments.SaveChangesAsync(ct);

        // Stamped only after the first event's transaction had committed.
        var stampedAfter = PaymentsEvent("com.corebank.account.balance.updated", DateTime.UtcNow.AddMilliseconds(50));
        payments.InboxMessages.Add(stampedAfter);
        await payments.SaveChangesAsync(ct);

        // Both completions in one transaction on purpose: reading the commit
        // timestamp off the row's latest version (xmin) would tie here.
        var now = DateTime.UtcNow;
        stampedAfter.Status = MessageConstants.Status.Completed;
        stampedAfter.ProcessedAt = now.AddSeconds(1);
        acceptedFirst.Status = MessageConstants.Status.Completed;
        acceptedFirst.ProcessedAt = now.AddSeconds(2);
        await payments.SaveChangesAsync(ct);

        var result = await GetResultsAsync(ct);

        result.Checks.PerKeyOrdering.Passed.Should().BeFalse();
        result.Debug.OrderingViolations.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Store = "PaymentsInbox", EarlierKey = TransactionId, LaterKey = TransactionId });
    }

    [Fact]
    public async Task A_corebank_inbox_command_accepted_before_the_next_was_stamped_but_processed_after_it_is_an_ordering_inversion()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var coreBank = CreateCoreBankContext();
        var acceptedFirst = CoreBankCommand("accepted-first", DateTime.UtcNow);
        coreBank.InboxMessages.Add(acceptedFirst);
        await coreBank.SaveChangesAsync(ct);

        var stampedAfter = CoreBankCommand("stamped-after", DateTime.UtcNow.AddMilliseconds(50));
        coreBank.InboxMessages.Add(stampedAfter);
        await coreBank.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;
        stampedAfter.Status = MessageConstants.Status.Completed;
        stampedAfter.ProcessedAt = now.AddSeconds(1);
        acceptedFirst.Status = MessageConstants.Status.Completed;
        acceptedFirst.ProcessedAt = now.AddSeconds(2);
        await coreBank.SaveChangesAsync(ct);

        var result = await GetResultsAsync(ct);

        result.Checks.PerKeyOrdering.Passed.Should().BeFalse();
        result.Debug.OrderingViolations.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Store = "CoreBankInbox", EarlierKey = "accepted-first", LaterKey = "stamped-after" });
        result.Checks.PerAccountOrdering.Passed.Should().BeFalse();
    }

    private async Task<AssertionResult> GetResultsAsync(CancellationToken ct)
    {
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        return await new LoadTestAssertionService(coreBank, payments).GetResultsAsync(expectedUnique: null, ct);
    }

    /// <summary>One CoreBank event for <see cref="TransactionId"/>; the three events of a transaction share its idempotency key and partition.</summary>
    private static PaymentsInboxMessage PaymentsEvent(string eventType, DateTime receivedAt) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = TransactionId,
        TransactionId = TransactionId,
        EventType = eventType,
        AccountNumber = DebtorAccount,
        Payload = "{}",
        PartitionId = PartitionHelper.GetPartitionId(TransactionId, 4),
        Status = MessageConstants.Status.Pending,
        ReceivedAt = receivedAt
    };

    private static CoreBankInboxMessage CoreBankCommand(string key, DateTime receivedAt) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = key,
        TransactionId = key,
        FromAccount = DebtorAccount,
        ToAccount = "NL02LOAD0000000002",
        Amount = 1m,
        Currency = "EUR",
        PartitionId = PartitionHelper.GetPartitionId(DebtorAccount, 4),
        Status = MessageConstants.Status.Pending,
        ReceivedAt = receivedAt
    };

    private CoreBankDbContext CreateCoreBankContext() =>
        new(new DbContextOptionsBuilder<CoreBankDbContext>().UseNpgsql(_coreBankConnectionString).Options);

    private PaymentsDbContext CreatePaymentsContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(_paymentsConnectionString).Options);
}
