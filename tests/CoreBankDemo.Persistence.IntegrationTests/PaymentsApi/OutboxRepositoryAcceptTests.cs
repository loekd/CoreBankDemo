using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Accounts;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

/// <summary>
/// The transactional-outbox accept (spec: payments-account-projection): the
/// outbox insert and the debtor's reservation commit together or not at all,
/// insert-first dedupe is kept, and a known-short account is refused without
/// leaving a row.
/// </summary>
public class OutboxRepositoryAcceptTests(PostgresContainerFixture fixture) : PaymentsPostgresTestBase(fixture)
{
    private const string Debtor = "NL91ABNA0417164300";

    [Fact]
    public async Task AcceptAsync_commits_the_outbox_row_and_the_reservation_together()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var outcome = await repository.AcceptAsync(PaymentsApiTestData.Outbox("accept-1"), ct);

        outcome.Should().Be(PaymentAcceptance.Stored);
        await using var verification = CreateContext();
        (await verification.OutboxMessages.CountAsync(m => m.IdempotencyKey == "accept-1", ct)).Should().Be(1);
        var account = await verification.ProjectedAccounts.SingleAsync(ct);
        account.AccountNumber.Should().Be(Debtor);
        account.SettledBalance.Should().BeNull("PaymentsAPI never seeds a balance");
        account.Reserved.Should().Be(12.34m);
    }

    [Fact]
    public async Task AcceptAsync_accumulates_reservations_for_a_known_account_with_enough_funds()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(settled: 100m, reserved: 50m, ct);
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var outcome = await repository.AcceptAsync(PaymentsApiTestData.Outbox("accept-2"), ct);

        outcome.Should().Be(PaymentAcceptance.Stored);
        await using var verification = CreateContext();
        (await verification.ProjectedAccounts.SingleAsync(ct)).Reserved.Should().Be(62.34m);
    }

    [Fact]
    public async Task AcceptAsync_refuses_a_known_short_account_and_leaves_nothing_behind()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(settled: 100m, reserved: 90m, ct); // available 10.00 < 12.34
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var outcome = await repository.AcceptAsync(PaymentsApiTestData.Outbox("refused"), ct);

        outcome.Should().Be(PaymentAcceptance.InsufficientFunds);
        await using var verification = CreateContext();
        (await verification.OutboxMessages.CountAsync(ct)).Should().Be(0, "a refused payment leaves no row");
        (await verification.ProjectedAccounts.SingleAsync(ct)).Reserved.Should().Be(90m, "and no reservation");
    }

    [Fact]
    public async Task AcceptAsync_accepts_exactly_the_available_amount()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(settled: 100m, reserved: 87.66m, ct); // available 12.34 == amount
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        (await repository.AcceptAsync(PaymentsApiTestData.Outbox("exact"), ct)).Should().Be(PaymentAcceptance.Stored);
    }

    [Fact]
    public async Task AcceptAsync_never_refuses_while_the_settled_balance_is_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(settled: null, reserved: 1_000_000m, ct);
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        (await repository.AcceptAsync(PaymentsApiTestData.Outbox("unknown"), ct)).Should().Be(PaymentAcceptance.Stored);
    }

    [Fact]
    public async Task AcceptAsync_duplicate_key_rolls_back_before_the_balance_is_consulted_and_keeps_the_context_usable()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(settled: 0m, reserved: 0m, ct); // would be refused if the balance were checked
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        await using (var seed = CreateContext())
        {
            seed.OutboxMessages.Add(PaymentsApiTestData.Outbox("dup"));
            await seed.SaveChangesAsync(ct);
        }

        var outcome = await repository.AcceptAsync(PaymentsApiTestData.Outbox("dup"), ct);

        outcome.Should().Be(PaymentAcceptance.Duplicate);
        var winner = await repository.FindByIdempotencyKeyAsync("dup", ct);
        winner.Should().NotBeNull("the context must still work after the rolled-back insert");
        await using var verification = CreateContext();
        (await verification.OutboxMessages.CountAsync(ct)).Should().Be(1);
        (await verification.ProjectedAccounts.SingleAsync(ct)).Reserved.Should().Be(0m);
    }

    [Fact]
    public async Task AcceptAsync_refused_key_is_not_consumed()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(settled: 10m, reserved: 0m, ct);
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        (await repository.AcceptAsync(PaymentsApiTestData.Outbox("later"), ct)).Should().Be(PaymentAcceptance.InsufficientFunds);

        await using (var funds = CreateContext())
        {
            var row = await funds.ProjectedAccounts.SingleAsync(ct);
            row.SettledBalance = 500m;
            await funds.SaveChangesAsync(ct);
        }

        (await repository.AcceptAsync(PaymentsApiTestData.Outbox("later"), ct)).Should().Be(PaymentAcceptance.Stored);
    }

    [Fact]
    public async Task AcceptAsync_second_accept_on_a_reused_context_sees_the_settlement_written_in_between()
    {
        // Review fix (2026-10-08): a second AcceptAsync on the same
        // repository/context must re-lock and re-read the row, not hand back
        // a stale in-memory ProjectedAccount from the first call. Chosen so
        // the stale balance (20.00) and the fresh one (50.00) disagree on the
        // second accept's outcome: stale leaves only 7.66 available (refuses
        // 12.34), fresh leaves 37.66 (accepts it).
        var ct = TestContext.Current.CancellationToken;
        await Seed(settled: 20m, reserved: 0m, ct);
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        (await repository.AcceptAsync(PaymentsApiTestData.Outbox("reuse-a"), ct)).Should().Be(PaymentAcceptance.Stored);

        await using (var settlement = CreateContext())
        {
            var row = await settlement.ProjectedAccounts.SingleAsync(ct);
            row.SettledBalance = 50m;
            await settlement.SaveChangesAsync(ct);
        }

        (await repository.AcceptAsync(PaymentsApiTestData.Outbox("reuse-b"), ct)).Should().Be(PaymentAcceptance.Stored,
            "the fresh row leaves 37.66 available; a stale cached row would wrongly refuse");

        await using var verification = CreateContext();
        var account = await verification.ProjectedAccounts.SingleAsync(ct);
        account.Reserved.Should().Be(24.68m, "both accepts' reservations landed");
        account.SettledBalance.Should().Be(50m, "the settlement the other context wrote was never clobbered");
    }

    [Fact]
    public async Task AcceptAsync_two_concurrent_debits_cannot_both_pass_on_the_same_funds()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(settled: 20m, reserved: 0m, ct); // room for one 12.34, not two
        await using var store = CreateStore();
        var (first, second) = store.CreateCompetingContexts();
        await using var firstHandle = first;
        await using var secondHandle = second;
        var firstRepository = new OutboxRepository(first, TimeProvider, TestBusinessMetrics.Instance);
        var secondRepository = new OutboxRepository(second, TimeProvider, TestBusinessMetrics.Instance);

        var outcomes = await PaymentsApiTestData.RaceAsync(
            () => firstRepository.AcceptAsync(PaymentsApiTestData.Outbox("race-a"), ct),
            () => secondRepository.AcceptAsync(PaymentsApiTestData.Outbox("race-b"), ct));

        outcomes.Should().BeEquivalentTo([PaymentAcceptance.Stored, PaymentAcceptance.InsufficientFunds]);
        await using var verification = CreateContext();
        (await verification.OutboxMessages.CountAsync(ct)).Should().Be(1);
        (await verification.ProjectedAccounts.SingleAsync(ct)).Reserved.Should().Be(12.34m);
    }

    [Fact]
    public async Task AcceptAsync_two_concurrent_first_debits_create_one_row_and_sum_reservations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = CreateStore();
        var (first, second) = store.CreateCompetingContexts();
        await using var firstHandle = first;
        await using var secondHandle = second;
        var firstRepository = new OutboxRepository(first, TimeProvider, TestBusinessMetrics.Instance);
        var secondRepository = new OutboxRepository(second, TimeProvider, TestBusinessMetrics.Instance);

        var outcomes = await PaymentsApiTestData.RaceAsync(
            () => firstRepository.AcceptAsync(PaymentsApiTestData.Outbox("first-a"), ct),
            () => secondRepository.AcceptAsync(PaymentsApiTestData.Outbox("first-b"), ct));

        outcomes.Should().AllBeEquivalentTo(PaymentAcceptance.Stored);
        await using var verification = CreateContext();
        var account = await verification.ProjectedAccounts.SingleAsync(ct);
        account.Reserved.Should().Be(24.68m);
    }

    private async Task Seed(decimal? settled, decimal reserved, CancellationToken ct)
    {
        await using var seed = CreateContext();
        seed.ProjectedAccounts.Add(new ProjectedAccount
        {
            AccountNumber = Debtor, SettledBalance = settled, Reserved = reserved, Currency = "EUR",
            UpdatedAt = TimeProvider.GetUtcNow().UtcDateTime
        });
        await seed.SaveChangesAsync(ct);
    }
}
