using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.LoadTestSupport.Services;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.LoadTestSupport;

/// <summary>
/// The CoreBank outbox lane orders the rows it can see by <c>CreatedAt</c>,
/// and it can only see committed rows. <c>OutboxEventEnqueuer</c> stamps
/// <c>CreatedAt</c> inside the executing transaction, which commits later
/// together with the inbox row and the balance update; two transactions
/// executing concurrently on different inbox partitions can therefore enqueue
/// events into one outbox partition in either stamp order, while a row
/// accepted (committed) before the next was even stamped must be published
/// first. The per-key check reads the accepting transaction's
/// <c>pg_xact_commit_timestamp</c> to tell the two apart, as it does for the
/// Payments outbox and both inboxes. Real PostgreSQL only.
/// </summary>
public sealed class CoreBankOutboxCommitOrderTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private const int Partition = 3;

    private string? _coreBankConnectionString;
    private string? _paymentsConnectionString;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _coreBankConnectionString = await fixture.CreateDatabaseAsync("cboutboxordercore", cancellationToken);
        _paymentsConnectionString = await fixture.CreateDatabaseAsync("cboutboxorderpay", cancellationToken);

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
    public async Task Two_events_enqueued_by_transactions_in_flight_at_the_same_time_owe_each_other_no_order(bool stampedFirstPublishedFirst)
    {
        var ct = TestContext.Current.CancellationToken;

        // Transaction A: its event is stamped first, but the transaction stays open.
        await using var slow = CreateCoreBankContext();
        await using var slowTransaction = await slow.Database.BeginTransactionAsync(ct);
        var stampedFirst = Event("load-test-0000000381", DateTime.UtcNow);
        slow.MessagingOutboxMessages.Add(stampedFirst);
        await slow.SaveChangesAsync(ct);

        // Transaction B: stamped while A is still open, committed immediately.
        await using var fast = CreateCoreBankContext();
        var committedFirst = Event("load-test-0000000383", DateTime.UtcNow);
        fast.MessagingOutboxMessages.Add(committedFirst);
        await fast.SaveChangesAsync(ct);

        // Only now does transaction A commit.
        await slowTransaction.CommitAsync(ct);

        var first = DateTime.UtcNow;
        var second = first.AddSeconds(1);
        committedFirst.Status = MessageConstants.Status.Completed;
        committedFirst.ProcessedAt = stampedFirstPublishedFirst ? second : first;
        await fast.SaveChangesAsync(ct);
        stampedFirst.Status = MessageConstants.Status.Completed;
        stampedFirst.ProcessedAt = stampedFirstPublishedFirst ? first : second;
        await slow.SaveChangesAsync(ct);

        var result = await GetResultsAsync(ct);

        result.Debug.OrderingViolations.Should().BeEmpty(
            "neither transaction committed before the other's event was stamped, so the lane owed them no order");
        result.Checks.PerKeyOrdering.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task An_event_committed_before_the_next_was_stamped_but_published_after_it_is_an_ordering_inversion()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var coreBank = CreateCoreBankContext();
        var acceptedFirst = Event("accepted-first", DateTime.UtcNow);
        coreBank.MessagingOutboxMessages.Add(acceptedFirst);
        await coreBank.SaveChangesAsync(ct);

        // Stamped only after the first transaction had committed.
        var stampedAfter = Event("stamped-after", DateTime.UtcNow.AddMilliseconds(50));
        coreBank.MessagingOutboxMessages.Add(stampedAfter);
        await coreBank.SaveChangesAsync(ct);

        // Both completions in one transaction on purpose: reading the commit
        // timestamp off the row's latest version (xmin) would tie here.
        var now = DateTime.UtcNow;
        stampedAfter.Status = MessageConstants.Status.Completed;
        stampedAfter.ProcessedAt = now.AddSeconds(1);
        acceptedFirst.Status = MessageConstants.Status.Completed;
        acceptedFirst.ProcessedAt = now.AddSeconds(2);
        await coreBank.SaveChangesAsync(ct);

        var result = await GetResultsAsync(ct);

        result.Checks.PerKeyOrdering.Passed.Should().BeFalse();
        result.Debug.OrderingViolations.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Store = "CoreBankOutbox", EarlierKey = "accepted-first", LaterKey = "stamped-after" });
    }

    private async Task<AssertionResult> GetResultsAsync(CancellationToken ct)
    {
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        return await new LoadTestAssertionService(coreBank, payments).GetResultsAsync(expectedUnique: null, ct);
    }

    /// <summary>One <c>transaction.completed</c> event; the two transactions' events share <see cref="Partition"/> on purpose.</summary>
    private static MessagingOutboxMessage Event(string transactionId, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = transactionId,
        TransactionId = transactionId,
        EventType = "com.corebank.transaction.completed",
        EventSource = "corebank-api",
        AccountNumber = "NL01LOAD0000000001",
        ToAccount = "NL02LOAD0000000002",
        Amount = 1m,
        Currency = "EUR",
        TransactionStatus = "Completed",
        EventOccurredAt = createdAt,
        PartitionId = Partition,
        Status = MessageConstants.Status.Pending,
        CreatedAt = createdAt
    };

    private CoreBankDbContext CreateCoreBankContext() =>
        new(new DbContextOptionsBuilder<CoreBankDbContext>().UseNpgsql(_coreBankConnectionString).Options);

    private PaymentsDbContext CreatePaymentsContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(_paymentsConnectionString).Options);
}
