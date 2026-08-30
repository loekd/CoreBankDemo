using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CoreBankDemo.Messaging.Tests;

/// <summary>
/// <see cref="UniqueViolation"/> (story 2.2): the single unique-constraint
/// detector call sites rely on instead of string-matching. This unit tier
/// covers the classification logic with typed <see cref="PostgresException"/>
/// instances and the shapes that must NOT be classified as duplicates; the
/// real-provider proof (a live PostgreSQL 23505 raised by an actual unique
/// index, and a real non-unique failure propagating) lives in
/// <c>CoreBankDemo.Persistence.IntegrationTests</c> (ADR-016).
/// </summary>
public class UniqueViolationTests
{
    [Fact]
    public void Null_exception_throws_argument_null()
    {
        var act = () => UniqueViolation.IsUniqueViolation(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("exception");
    }

    [Fact]
    public void Postgres_unique_violation_sqlstate_is_detected()
    {
        var postgresException = new PostgresException(
            messageText: "duplicate key value violates unique constraint",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.UniqueViolation);
        var dbUpdateException = new DbUpdateException("Save failed.", postgresException);

        UniqueViolation.IsUniqueViolation(dbUpdateException).Should().BeTrue();
    }

    [Theory]
    [InlineData(PostgresErrorCodes.NotNullViolation)]
    [InlineData(PostgresErrorCodes.CheckViolation)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    public void Postgres_non_unique_sqlstates_are_not_detected(string sqlState)
    {
        var postgresException = new PostgresException(
            messageText: "some other constraint failed",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: sqlState);
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

    [Fact]
    public void Non_postgres_database_exception_is_not_a_unique_violation()
    {
        // No provider sniffing by type name or message text: an exception that
        // is not a PostgresException is simply not a duplicate, whatever its
        // message claims.
        var dbUpdateException = new DbUpdateException(
            "Save failed.",
            new InvalidOperationException("UNIQUE constraint failed: InboxMessages.IdempotencyKey"));

        UniqueViolation.IsUniqueViolation(dbUpdateException).Should().BeFalse();
    }
}
