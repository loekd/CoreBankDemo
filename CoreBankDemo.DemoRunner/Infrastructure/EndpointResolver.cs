using CoreBankDemo.DemoRunner.Application.Scenarios;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>
/// The single source of truth mapping known resource/endpoint/link ids to their real
/// local URLs. Scenario data never supplies a URL directly — only these compiled ids
/// (ADR-015). Ports match the documented values in <c>docs/bmad/constraints.md</c> and
/// the brownfield README.
/// </summary>
public static class EndpointResolver
{
    private const string PaymentsApiBaseUrl = "http://127.0.0.1:5294";
    private const string CoreBankApiBaseUrl = "http://127.0.0.1:5032";
    private const string LoadTestSupportBaseUrl = "http://localhost:5181";
    private const string JaegerBaseUrl = "http://localhost:16686";
    private const string AspireDashboardBaseUrl = "http://localhost:15888";

    public static string HealthUrlFor(string resourceName) => resourceName switch
    {
        KnownResources.PaymentsApi => $"{PaymentsApiBaseUrl}/health",
        KnownResources.CoreBankApi => $"{CoreBankApiBaseUrl}/health",
        KnownResources.LoadTestSupport => $"{LoadTestSupportBaseUrl}/health",
        KnownResources.Jaeger => $"{JaegerBaseUrl}/",
        KnownResources.AspireDashboard => $"{AspireDashboardBaseUrl}/",
        // Postgres, Redis, and Dapr are not directly HTTP-probed by the console
        // (ADR-015 forbids connecting to their sockets); their confidence status is
        // reported via the owning API's health check instead.
        KnownResources.Postgres => $"{CoreBankApiBaseUrl}/health",
        KnownResources.Redis => $"{CoreBankApiBaseUrl}/health",
        KnownResources.Dapr => $"{CoreBankApiBaseUrl}/health",
        _ => throw new ArgumentOutOfRangeException(nameof(resourceName), resourceName, "Unknown resource."),
    };

    public static (string Url, HttpMethod Method) EndpointFor(string endpointId) => endpointId switch
    {
        KnownEndpoints.PaymentsSubmit => ($"{PaymentsApiBaseUrl}/api/payments", HttpMethod.Post),
        KnownEndpoints.PaymentsInbox => ($"{PaymentsApiBaseUrl}/api/inbox", HttpMethod.Get),
        KnownEndpoints.CoreBankTransactionsProcess => ($"{CoreBankApiBaseUrl}/api/transactions/process", HttpMethod.Post),
        KnownEndpoints.LoadTestSupportReset => ($"{LoadTestSupportBaseUrl}/reset", HttpMethod.Post),
        KnownEndpoints.LoadTestSupportDrain => ($"{LoadTestSupportBaseUrl}/assert/drain", HttpMethod.Get),
        KnownEndpoints.LoadTestSupportAssert => ($"{LoadTestSupportBaseUrl}/assert/results", HttpMethod.Get),
        KnownEndpoints.LoadTestSupportCoreBankInbox => ($"{LoadTestSupportBaseUrl}/corebank/inbox", HttpMethod.Get),
        _ => throw new ArgumentOutOfRangeException(nameof(endpointId), endpointId, "Unknown endpoint."),
    };

    public static string LinkFor(string linkId) => linkId switch
    {
        KnownLinks.AspireDashboard => $"{AspireDashboardBaseUrl}/",
        KnownLinks.Jaeger => $"{JaegerBaseUrl}/",
        KnownLinks.RepoGitHub => "https://github.com/loekd/CoreBankDemo",
        KnownLinks.DevContainerDocs => "https://docs.github.com/en/codespaces/overview",
        _ => throw new ArgumentOutOfRangeException(nameof(linkId), linkId, "Unknown link."),
    };

    public static readonly IReadOnlyDictionary<string, int> RegularProfilePorts = new Dictionary<string, int>
    {
        [KnownResources.PaymentsApi] = 5294,
        [KnownResources.CoreBankApi] = 5032,
        [KnownResources.Jaeger] = 16686,
        [KnownResources.AspireDashboard] = 15888,
    };

    public static readonly IReadOnlyDictionary<string, int> LoadTestProfilePorts = new Dictionary<string, int>
    {
        [KnownResources.LoadTestSupport] = 5181,
        [KnownResources.CoreBankApi] = 5032,
    };
}
