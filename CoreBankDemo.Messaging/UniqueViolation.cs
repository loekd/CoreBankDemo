using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreBankDemo.Messaging;

/// <summary>
/// Provider-aware unique-constraint-violation detector (AD-9): the single place
/// callers ask "did this <see cref="DbUpdateException"/> come from a duplicate-key
/// race?" — never string-matching or provider-specific checks at call sites.
/// Postgres detection uses <see cref="PostgresException"/> directly (Npgsql is
/// already a package reference of this project). SQLite detection inspects the
/// inner exception's runtime shape (type name and property names) via
/// reflection instead, so this project incurs no package reference to
/// Microsoft.Data.Sqlite — that dependency belongs to the test tier only
/// (AD-9: SQLite is the store test tier, not a runtime provider).
/// </summary>
public static class UniqueViolation
{
    private const string SqliteExceptionTypeName = "Microsoft.Data.Sqlite.SqliteException";

    // SQLite primary result code for any constraint violation (foreign key,
    // check, unique, not-null, ...).
    private const int SqliteConstraint = 19;

    // SQLite extended result code specifically for a violated UNIQUE (or
    // PRIMARY KEY) index — the case this helper exists to detect.
    private const int SqliteConstraintUnique = 2067;

    /// <summary>
    /// True when <paramref name="exception"/> wraps a unique/primary-key
    /// constraint violation raised by SQLite (result code 19, extended code
    /// 2067) or Postgres (SQLSTATE 23505). Any other <see cref="DbUpdateException"/>
    /// — including one with no inner exception, or an inner exception from
    /// neither provider — returns <see langword="false"/> so callers rethrow it
    /// unchanged (spec: non-unique-violation failures propagate).
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.InnerException switch
        {
            PostgresException postgres => postgres.SqlState == PostgresErrorCodes.UniqueViolation,
            { } inner => IsSqliteUniqueViolation(inner),
            null => false,
        };
    }

    private static bool IsSqliteUniqueViolation(Exception inner)
    {
        var type = inner.GetType();
        if (type.FullName != SqliteExceptionTypeName)
        {
            return false;
        }

        var errorCode = type.GetProperty("SqliteErrorCode", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(inner) as int?;
        var extendedErrorCode = type.GetProperty("SqliteExtendedErrorCode", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(inner) as int?;

        // Both codes must match: the primary code (19) is shared by every SQLite
        // constraint kind (NOT NULL, CHECK, FK, PRIMARY KEY, UNIQUE, ...); only
        // the extended code (2067) pinpoints a violated UNIQUE index. Checking
        // the primary code alone would misclassify e.g. a NOT NULL violation as
        // a duplicate-key race.
        return errorCode == SqliteConstraint && extendedErrorCode == SqliteConstraintUnique;
    }
}
