using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.LoadTestSupport.Services;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.LoadTestSupport;

/// <summary>
/// The Payments outbox lane orders the rows it can see by <c>CreatedAt</c>,
/// and it can only see committed rows. <c>PaymentStorageHandler</c> stamps
/// before <c>OutboxRepository.AcceptAsync</c> opens its transaction, so two
/// requests in flight at the same time may be forwarded in either order,
/// while a row accepted (committed) before the next was even stamped must be
/// forwarded first -- docs/constraints.md's "in the order they were
/// accepted". The per-key and per-account checks read the accepting
/// transaction's <c>pg_xact_commit_timestamp</c> to tell the two apart. Real
/// PostgreSQL only: the commit timestamp is a server feature.
/// </summary>
public sealed class PaymentsOutboxCommitOrderTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private const string DebtorAccount = "NL01LOAD0000000001";

    private string? _coreBankConnectionString;
    private string? _paymentsConnectionString;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _coreBankConnectionString = await fixture.CreateDatabaseAsync("commitordercore", cancellationToken);
        _paymentsConnectionString = await fixture.CreateDatabaseAsync("commitorderpay", cancellationToken);

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
    public async Task Two_requests_in_flight_at_the_same_time_owe_each_other_no_order(bool stampedFirstForwardedFirst)
    {
        var ct = TestContext.Current.CancellationToken;

        // Request A: stamped first, but its accepting transaction stays open.
        await using var slow = CreatePaymentsContext();
        await using var slowTransaction = await slow.Database.BeginTransactionAsync(ct);
        var stampedFirst = Outbox("stamped-first", DateTime.UtcNow);
        slow.OutboxMessages.Add(stampedFirst);
        await slow.SaveChangesAsync(ct);

        // Request B: stamped while A is still open, committed immediately.
        await using var fast = CreatePaymentsContext();
        var committedFirst = Outbox("committed-first", DateTime.UtcNow);
        fast.OutboxMessages.Add(committedFirst);
        await fast.SaveChangesAsync(ct);

        // Only now does request A commit.
        await slowTransaction.CommitAsync(ct);

        var first = DateTime.UtcNow;
        var second = first.AddSeconds(1);
        committedFirst.Status = MessageConstants.Status.Completed;
        committedFirst.ProcessedAt = stampedFirstForwardedFirst ? second : first;
        await fast.SaveChangesAsync(ct);
        stampedFirst.Status = MessageConstants.Status.Completed;
        stampedFirst.ProcessedAt = stampedFirstForwardedFirst ? first : second;
        await slow.SaveChangesAsync(ct);

        var result = await GetResultsAsync(ct);

        result.Debug.OrderingViolations.Should().BeEmpty(
            "neither request was accepted before the other was stamped, so the lane owed them no order");
        result.Checks.PerKeyOrdering.Passed.Should().BeTrue();
        result.Debug.AccountOrderingViolations.Should().BeEmpty();
        result.Checks.PerAccountOrdering.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task A_row_accepted_before_the_next_was_stamped_but_forwarded_after_it_is_an_ordering_inversion()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var payments = CreatePaymentsContext();
        var acceptedFirst = Outbox("accepted-first", DateTime.UtcNow);
        payments.OutboxMessages.Add(acceptedFirst);
        await payments.SaveChangesAsync(ct);

        // Stamped only after the first request's transaction had committed.
        var stampedAfter = Outbox("stamped-after", DateTime.UtcNow.AddMilliseconds(50));
        payments.OutboxMessages.Add(stampedAfter);
        await payments.SaveChangesAsync(ct);

        // Both forwards are recorded in one transaction on purpose: an
        // implementation reading the commit timestamp off the row's latest
        // version (xmin) would see a tie here and miss the inversion.
        var now = DateTime.UtcNow;
        stampedAfter.Status = MessageConstants.Status.Completed;
        stampedAfter.ProcessedAt = now.AddSeconds(1);
        acceptedFirst.Status = MessageConstants.Status.Completed;
        acceptedFirst.ProcessedAt = now.AddSeconds(2);
        await payments.SaveChangesAsync(ct);

        var result = await GetResultsAsync(ct);

        result.Checks.PerKeyOrdering.Passed.Should().BeFalse();
        result.Debug.OrderingViolations.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Store = "PaymentsOutbox", EarlierKey = "accepted-first", LaterKey = "stamped-after" });
        result.Checks.PerAccountOrdering.Passed.Should().BeFalse();
    }

    private async Task<AssertionResult> GetResultsAsync(CancellationToken ct)
    {
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        return await new LoadTestAssertionService(coreBank, payments).GetResultsAsync(expectedUnique: null, ct);
    }

    private static OutboxMessage Outbox(string key, DateTime createdAt) => new()
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
        CreatedAt = createdAt
    };

    private CoreBankDbContext CreateCoreBankContext() =>
        new(new DbContextOptionsBuilder<CoreBankDbContext>().UseNpgsql(_coreBankConnectionString).Options);

    private PaymentsDbContext CreatePaymentsContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(_paymentsConnectionString).Options);
}
