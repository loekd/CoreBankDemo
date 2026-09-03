namespace CoreBankDemo.DemoRunner.Application;

public static class KnownTopologyProfiles
{
    public static readonly IReadOnlyList<TopologyProfile> All =
        [TopologyProfile.Regular, TopologyProfile.LoadTests];

    public static string DisplayName(TopologyProfile profile) => profile switch
    {
        TopologyProfile.Regular => "Regular",
        TopologyProfile.LoadTests => "LoadTests",
        _ => "None",
    };
}

public static class KnownResources
{
    public const string PaymentsApi = "payments-api";
    public const string CoreBankApi = "corebank-api";
    public const string Postgres = "postgres";
    public const string Redis = "redis";
    public const string Jaeger = "jaeger";
    public const string DevProxy = "devproxy";
    public const string LoadTestSupport = "loadtest-support";
    public const string LoadTestInitializer = "loadtest-initializer";
    public const string K6 = "k6";
    public const string AspireDashboard = "aspire-dashboard";

    public static readonly IReadOnlySet<string> ResourceCommandAllowList = new HashSet<string>(
        [
            PaymentsApi,
            CoreBankApi,
            Postgres,
            Redis,
            Jaeger,
            DevProxy,
            LoadTestSupport,
            K6,
        ],
        StringComparer.Ordinal);

    public static IReadOnlySet<string> RequiredFor(TopologyProfile profile) => profile switch
    {
        TopologyProfile.Regular => new HashSet<string>(
            [PaymentsApi, CoreBankApi, Postgres, Redis, Jaeger],
            StringComparer.Ordinal),
        TopologyProfile.LoadTests => new HashSet<string>(
            [PaymentsApi, CoreBankApi, Postgres, Redis, Jaeger, LoadTestSupport, LoadTestInitializer, K6],
            StringComparer.Ordinal),
        _ => new HashSet<string>(StringComparer.Ordinal),
    };

    public static int ExpectedReplicaCount(string resourceName) =>
        resourceName is PaymentsApi or CoreBankApi ? 2 : 1;
}

public static class KnownEndpoints
{
    public const string PaymentsSubmit = "payments.submit";
    public const string TransactionOutcome = "corebank.transactions.status";
    public const string LoadReset = "loadtestsupport.reset";
    public const string LoadDrain = "loadtestsupport.drain";
    public const string LoadAssert = "loadtestsupport.assert";
    public const string PaymentsOutbox = "loadtestsupport.payments.outbox";
    public const string PaymentsInbox = "loadtestsupport.payments.inbox";
    public const string CoreBankInbox = "loadtestsupport.corebank.inbox";
    public const string CoreBankOutbox = "loadtestsupport.corebank.outbox";
}

public static class KnownLinks
{
    public const string AspireDashboard = "aspire-dashboard";
    public const string Jaeger = "jaeger";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [AspireDashboard, Jaeger],
        StringComparer.Ordinal);
}
