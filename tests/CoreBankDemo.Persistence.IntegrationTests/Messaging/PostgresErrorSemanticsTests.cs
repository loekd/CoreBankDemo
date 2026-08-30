using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Messaging;

/// <summary>
/// Real-provider error semantics (ADR-016): the exceptions
/// <see cref="UniqueViolation"/> classifies are produced by an actual
/// PostgreSQL server through actual indexes and constraints — never faked,
/// never matched on message text.
/// </summary>
public class PostgresErrorSemanticsTests(PostgresContainerFixture fixture)
    : MessagingPostgresTestBase(fixture)
{
    [Fact]
    public async Task Racing_inserts_on_a_real_unique_index_surface_sqlstate_23505_and_classify_as_duplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        const string key = "real-23505";

        // Two independent connections/contexts genuinely compete for the same
        // unique index entry: one wins, the loser gets the server's error.
        await using var winner = CreateContext();
        await using var loser = CreateContext();
        winner.InboxMessages.Add(new TestInboxMessage { IdempotencyKey = key });
        loser.InboxMessages.Add(new TestInboxMessage { IdempotencyKey = key });

        await winner.SaveChangesAsync(ct);
        var act = async () => await loser.SaveChangesAsync(ct);

        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        thrown.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation).And.Be("23505");
        UniqueViolation.IsUniqueViolation(thrown.Which).Should().BeTrue();
    }

    [Fact]
    public async Task A_real_check_constraint_violation_is_not_classified_as_a_duplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        context.InboxMessages.Add(new TestInboxMessage { IdempotencyKey = "check-violation", RetryCount = -1 });

        var act = async () => await context.SaveChangesAsync(ct);

        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        thrown.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        UniqueViolation.IsUniqueViolation(thrown.Which).Should().BeFalse();
    }

    [Fact]
    public async Task A_real_non_unique_failure_propagates_unchanged_out_of_StoreIfNewAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider);

        var act = async () => await repository.StoreIfNewAsync(
            new TestInboxMessage { IdempotencyKey = "propagates", RetryCount = -1 }, ct);

        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        thrown.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().NotBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task A_connection_level_failure_is_not_translated_into_a_duplicate_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var unreachable = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = "database-that-does-not-exist",
            Timeout = 5
        }.ConnectionString;

        await using var context = new TestMessagingDbContext(
            new DbContextOptionsBuilder<TestMessagingDbContext>().UseNpgsql(unreachable).Options);
        var repository = new TestInboxMessageRepository(context, TimeProvider);

        var act = async () => await repository.StoreIfNewAsync(
            new TestInboxMessage { IdempotencyKey = "unreachable" }, ct);

        // Not a DbUpdateException at all — the connection failure surfaces raw,
        // and is never reported as "already exists".
        await act.Should().ThrowAsync<PostgresException>();
    }
}
