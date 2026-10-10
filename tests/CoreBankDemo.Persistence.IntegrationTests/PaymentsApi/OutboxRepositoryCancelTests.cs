using System.Data.Common;
using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.PaymentsAPI.Accounts;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

/// <summary>
/// The instant rail's never-forwarded cancel on real PostgreSQL (spec:
/// payments-account-projection): the row's cancellation and the release of
/// its reservation commit together, and only an applied cancel releases.
/// </summary>
public class OutboxRepositoryCancelTests(PostgresContainerFixture fixture) : PaymentsPostgresTestBase(fixture)
{
    private const string Debtor = "NL91ABNA0417164300";
    private const string Reason = "never left PaymentsAPI";
    private const string CancelledPayload = """{"TransactionId":"cancel-key","Status":"Cancelled","ProcessedAt":"2026-08-28T12:00:00+00:00"}""";

    [Fact]
    public async Task CancelLocallyAsync_cancels_the_row_and_releases_its_reservation_in_one_commit()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(reserved: 12.34m, ct);
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var claimed = await ClaimAsync(context, ct);

        var outcome = await repository.CancelLocallyAsync(claimed, Reason, ct);

        outcome.Should().Be(MessageTransitionOutcome.Applied);
        await using var verification = CreateContext();
        var row = await verification.OutboxMessages.SingleAsync(ct);
        row.Status.Should().Be(MessageConstants.Status.Cancelled);
        row.LastError.Should().Be(Reason);
        row.ResponsePayload.Should().Be(CancelledPayload);
        row.ProcessedAt.Should().Be(TimeProvider.GetUtcNow().UtcDateTime);
        var account = await verification.ProjectedAccounts.SingleAsync(ct);
        account.Reserved.Should().Be(0m);
        account.UpdatedAt.Should().Be(TimeProvider.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task CancelLocallyAsync_on_an_already_cancelled_row_releases_nothing_a_second_time()
    {
        var ct = TestContext.Current.CancellationToken;
        // Room for a second release to show: a double release would leave 0.
        await SeedAsync(reserved: 24.68m, ct);
        await using (var first = CreateContext())
        {
            var repository = new OutboxRepository(first, TimeProvider, TestBusinessMetrics.Instance);
            (await repository.CancelLocallyAsync(await ClaimAsync(first, ct), Reason, ct)).Should().Be(MessageTransitionOutcome.Applied);
        }

        await using var context = CreateContext();
        var again = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var cancelled = await context.OutboxMessages.SingleAsync(ct);

        var outcome = await again.CancelLocallyAsync(cancelled, Reason, ct);

        outcome.Should().Be(MessageTransitionOutcome.AlreadyTerminal);
        await using var verification = CreateContext();
        (await verification.ProjectedAccounts.SingleAsync(ct)).Reserved.Should().Be(12.34m, "only the applied cancel released");
    }

    [Fact]
    public async Task CancelLocallyAsync_retried_by_the_execution_strategy_still_cancels_and_releases_once()
    {
        // Npgsql retry-on-failure (on by default under Aspire) re-runs the
        // transaction on the same context. Here the first attempt's cancel is
        // saved, its release save fails transiently and everything rolls back;
        // the retry must start from the claimed row, not from the rolled-back
        // attempt's in-memory Cancelled.
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(reserved: 24.68m, ct);
        await using var connectionOwner = CreateContext();
        await using var context = new TransientSaveFailureContext(connectionOwner.Database.GetDbConnection(), failingSave: 2);
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var claimed = await ClaimAsync(context, ct);

        var outcome = await repository.CancelLocallyAsync(claimed, Reason, ct);

        outcome.Should().Be(MessageTransitionOutcome.Applied);
        context.Saves.Should().Be(4, "the transient failure was retried once");
        await using var verification = CreateContext();
        var row = await verification.OutboxMessages.SingleAsync(ct);
        row.Status.Should().Be(MessageConstants.Status.Cancelled);
        row.LastError.Should().Be(Reason);
        row.ResponsePayload.Should().Be(CancelledPayload);
        (await verification.ProjectedAccounts.SingleAsync(ct)).Reserved.Should().Be(12.34m, "released exactly once");
    }

    private async Task SeedAsync(decimal reserved, CancellationToken ct)
    {
        await using var seed = CreateContext();
        seed.ProjectedAccounts.Add(new ProjectedAccount
        {
            AccountNumber = Debtor, SettledBalance = 100m, Reserved = reserved, Currency = "EUR",
            UpdatedAt = new DateTime(2026, 8, 28, 11, 0, 0, DateTimeKind.Utc)
        });
        var message = PaymentsApiTestData.Outbox("cancel-key"); // Debtor, 12.34
        message.Status = MessageConstants.Status.Processing;
        seed.OutboxMessages.Add(message);
        await seed.SaveChangesAsync(ct);
    }

    /// <summary>The row as the instant rail holds it: tracked, claimed, its cancelled payload already cached.</summary>
    private static async Task<OutboxMessage> ClaimAsync(PaymentsDbContext context, CancellationToken ct)
    {
        var claimed = await context.OutboxMessages.SingleAsync(ct);
        claimed.ResponsePayload = CancelledPayload;
        return claimed;
    }

    private sealed class SimulatedTransientException() : Exception("simulated transient failure");

    /// <summary>Retries <see cref="SimulatedTransientException"/> once, as Npgsql's strategy retries a transient fault.</summary>
    private sealed class RetryOnceStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is SimulatedTransientException;
    }

    /// <summary>Shares the given connection; the <paramref name="failingSave"/>-th save throws a transient failure, once.</summary>
    private sealed class TransientSaveFailureContext(DbConnection connection, int failingSave)
        : PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.ExecutionStrategy(dependencies => new RetryOnceStrategy(dependencies)))
            .Options)
    {
        public int Saves { get; private set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saves++;
            return Saves == failingSave
                ? throw new SimulatedTransientException()
                : base.SaveChangesAsync(cancellationToken);
        }
    }
}
