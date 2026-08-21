using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CoreBankDemo.Messaging.Tests;

/// <summary>
/// <see cref="UniqueViolation"/> (story 2.2): the single provider-aware
/// unique-constraint detector call sites rely on instead of string-matching or
/// provider-specific checks. Exercised against a real SQLite violation (store
/// test tier), a faked Npgsql-shaped exception (no live Postgres in this
/// tier — AD-9), and the shapes that must NOT be classified as duplicates.
/// </summary>
public class UniqueViolationTests : SqliteMessagingTestBase
{
    [Fact]
    public void Null_exception_throws_argument_null()
    {
        var act = () => UniqueViolation.IsUniqueViolation(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("exception");
    }

    [Fact]
    public async Task Real_sqlite_unique_violation_is_detected()
    {
        await using var context = CreateContext();
        context.InboxMessages.Add(new TestInboxMessage { IdempotencyKey = "dup-key" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.InboxMessages.Add(new TestInboxMessage { IdempotencyKey = "dup-key" });
        var act = async () => await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        UniqueViolation.IsUniqueViolation(thrown.Which).Should().BeTrue();
    }

    [Fact]
    public async Task Real_sqlite_check_constraint_violation_is_not_a_unique_violation()
    {
        // Same base SQLite result code (19) as a UNIQUE violation, but a
        // different extended code (CHECK, not UNIQUE) — proves the helper
        // distinguishes constraint kinds rather than matching on code 19 alone.
        await using var context = CreateContext();
        context.InboxMessages.Add(new TestInboxMessage { IdempotencyKey = "any-key", RetryCount = -1 });
        var act = async () => await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        UniqueViolation.IsUniqueViolation(thrown.Which).Should().BeFalse();
    }

    [Fact]
    public void Faked_postgres_unique_violation_is_detected()
    {
        var postgresException = new PostgresException(
            messageText: "duplicate key value violates unique constraint",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.UniqueViolation);
        var dbUpdateException = new DbUpdateException("Save failed.", postgresException);

        UniqueViolation.IsUniqueViolation(dbUpdateException).Should().BeTrue();
    }

    [Fact]
    public void Faked_postgres_non_unique_violation_is_not_detected()
    {
        var postgresException = new PostgresException(
            messageText: "null value in column violates not-null constraint",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.NotNullViolation);
        var dbUpdateException = new DbUpdateException("Save failed.", postgresException);

        UniqueViolation.IsUniqueViolation(dbUpdateException).Should().BeFalse();
    }

    [Fact]
    public void DbUpdateException_with_no_inner_exception_is_not_a_unique_violation()
    {
        var dbUpdateException = new DbUpdateException("Save failed.");

        UniqueViolation.IsUniqueViolation(dbUpdateException).Should().BeFalse();
    }

    [Fact]
    public void DbUpdateException_wrapping_unrelated_exception_is_not_a_unique_violation()
    {
        var dbUpdateException = new DbUpdateException("Save failed.", new InvalidOperationException("boom"));

        UniqueViolation.IsUniqueViolation(dbUpdateException).Should().BeFalse();
    }
}
