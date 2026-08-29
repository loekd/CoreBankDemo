namespace CoreBankDemo.DemoRunner.Application.Scenarios;

/// <summary>
/// Compiled allow-list of known Aspire AppHost profiles a scenario may select. This is
/// application configuration, not a scenario-supplied command (ADR-015).
/// </summary>
public static class KnownTopologyProfiles
{
    public const string Regular = "Regular";
    public const string LoadTest = "LoadTest";

    public static readonly IReadOnlySet<string> All = new HashSet<string>([Regular, LoadTest], StringComparer.Ordinal);
}

/// <summary>Compiled allow-list of resources the health monitor and confidence pane know about.</summary>
public static class KnownResources
{
    public const string PaymentsApi = "payments-api";
    public const string CoreBankApi = "corebank-api";
    public const string Postgres = "postgres";
    public const string Redis = "redis";
    public const string Dapr = "dapr";
    public const string Jaeger = "jaeger";
    public const string LoadTestSupport = "loadtest-support";
    public const string AspireDashboard = "aspire-dashboard";

    public static readonly IReadOnlyList<string> All =
    [
        PaymentsApi, CoreBankApi, Postgres, Redis, Dapr, Jaeger, LoadTestSupport, AspireDashboard,
    ];
}

/// <summary>
/// Compiled allow-list of local HTTP endpoints a sendHttp/assertHttp action may target.
/// A scenario references these by id only; the base URL/path is resolved by
/// Infrastructure, never supplied by scenario data.
/// </summary>
public static class KnownEndpoints
{
    public const string PaymentsSubmit = "payments.submit";
    public const string PaymentsInbox = "payments.inbox";
    public const string CoreBankTransactionsProcess = "corebank.transactions.process";
    public const string LoadTestSupportReset = "loadtestsupport.reset";
    public const string LoadTestSupportDrain = "loadtestsupport.drain";
    public const string LoadTestSupportAssert = "loadtestsupport.assert";
    public const string LoadTestSupportCoreBankInbox = "loadtestsupport.corebank.inbox";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
    [
        PaymentsSubmit, PaymentsInbox, CoreBankTransactionsProcess,
        LoadTestSupportReset, LoadTestSupportDrain, LoadTestSupportAssert, LoadTestSupportCoreBankInbox,
    ], StringComparer.Ordinal);
}

/// <summary>Compiled allow-list of URLs an openKnownUrl action may open in the browser.</summary>
public static class KnownLinks
{
    public const string AspireDashboard = "aspire-dashboard";
    public const string Jaeger = "jaeger";
    public const string RepoGitHub = "repo-github";
    public const string DevContainerDocs = "devcontainer-docs";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [AspireDashboard, Jaeger, RepoGitHub, DevContainerDocs], StringComparer.Ordinal);
}
