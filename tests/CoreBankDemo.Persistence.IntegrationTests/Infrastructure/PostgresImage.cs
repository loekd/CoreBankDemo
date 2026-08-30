namespace CoreBankDemo.Persistence.IntegrationTests.Infrastructure;

/// <summary>
/// The one place the persistence tier names its database engine (ADR-016).
/// The tag is pinned explicitly — never <c>latest</c>, never implicit — and
/// must stay on the same major version as the AppHost's
/// <c>AddPostgres(...).WithImageTag(...)</c> pin so provider fidelity is real.
/// </summary>
internal static class PostgresImage
{
    /// <summary>Pinned PostgreSQL image (ADR-016); mirrored by <c>CoreBankDemo.AppHost/AppHost.cs</c>.</summary>
    internal const string Tag = "postgres:18.3";
}
