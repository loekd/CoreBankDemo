using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreBankDemo.Messaging;

/// <summary>
/// Unique-constraint-violation detector (ADR-016): the single place callers ask
/// "did this <see cref="DbUpdateException"/> come from a duplicate-key race?" —
/// never string-matching, never provider sniffing at call sites.
/// </summary>
/// <remarks>
/// PostgreSQL is the only relational engine this system runs on, in production
/// and in tests alike, so detection is a typed <see cref="PostgresException"/>
/// SQLSTATE comparison against <see cref="PostgresErrorCodes.UniqueViolation"/>
/// (<c>23505</c>). Every other failure — a different SQLSTATE, a non-Npgsql
/// inner exception, or no inner exception at all — is not a duplicate, so
/// callers rethrow it unchanged.
/// </remarks>
public static class UniqueViolation
{
    /// <summary>
    /// True only when <paramref name="exception"/> wraps a PostgreSQL
    /// unique/primary-key violation (SQLSTATE <c>23505</c>).
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.InnerException is PostgresException postgres
            && postgres.SqlState == PostgresErrorCodes.UniqueViolation;
    }
}
