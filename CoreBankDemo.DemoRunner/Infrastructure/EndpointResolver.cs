using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>
/// The single source of truth mapping known resource/endpoint/link ids to their real
/// local URLs. Operator input never supplies a URL directly (ADR-015).
/// </summary>
public static class EndpointResolver
{
    private const string RegularPaymentsApiBaseUrl = "http://127.0.0.1:5294";
    private const string LoadPaymentsApiBaseUrl = "http://127.0.0.1:5295";
    private const string CoreBankApiBaseUrl = "http://127.0.0.1:5032";
    private const string LoadTestSupportBaseUrl = "http://localhost:5181";

    // Probing goes to the literal loopback address, like every other health probe here, so
    // the result never depends on how the machine orders "localhost" across address
    // families; the browser link keeps the friendlier hostname.
    private const string JaegerProbeUrl = "http://127.0.0.1:16686/";
    private const string JaegerLinkUrl = "http://localhost:16686/";

    public static string HealthUrlFor(string resourceName, TopologyProfile profile = TopologyProfile.Regular) => resourceName switch
    {
        KnownResources.PaymentsApi => $"{PaymentsBaseUrl(profile)}/health",
        KnownResources.CoreBankApi => $"{CoreBankApiBaseUrl}/health",
        KnownResources.LoadTestSupport => $"{LoadTestSupportBaseUrl}/health",
        KnownResources.Jaeger => JaegerProbeUrl,
        // Postgres, Redis, and Dapr are not directly HTTP-probed by the console
        // (ADR-015 forbids connecting to their sockets); their confidence status is
        // reported via the owning API's health check instead.
        KnownResources.Postgres => $"{CoreBankApiBaseUrl}/health",
        KnownResources.Redis => $"{CoreBankApiBaseUrl}/health",
        _ => throw new ArgumentOutOfRangeException(nameof(resourceName), resourceName, "Unknown resource."),
    };

    public static (string Url, HttpMethod Method) EndpointFor(
        TopologyProfile profile,
        string endpointId,
        string? pathParameter = null) => endpointId switch
    {
        KnownEndpoints.PaymentsSubmit => ($"{PaymentsBaseUrl(profile)}/api/payments", HttpMethod.Post),
        KnownEndpoints.TransactionOutcome => (
            $"{CoreBankApiBaseUrl}/api/transactions/{Uri.EscapeDataString(RequirePathParameter(endpointId, pathParameter))}",
            HttpMethod.Get),
        KnownEndpoints.LoadReset when profile == TopologyProfile.LoadTests => ($"{LoadTestSupportBaseUrl}/reset", HttpMethod.Post),
        KnownEndpoints.LoadDrain when profile == TopologyProfile.LoadTests => ($"{LoadTestSupportBaseUrl}/assert/drain", HttpMethod.Get),
        KnownEndpoints.LoadAssert when profile == TopologyProfile.LoadTests => ($"{LoadTestSupportBaseUrl}/assert/results", HttpMethod.Get),
        KnownEndpoints.PaymentsOutbox when profile == TopologyProfile.LoadTests => ($"{LoadTestSupportBaseUrl}/payments/outbox", HttpMethod.Get),
        KnownEndpoints.PaymentsInbox when profile == TopologyProfile.LoadTests => ($"{LoadTestSupportBaseUrl}/payments/inbox", HttpMethod.Get),
        KnownEndpoints.CoreBankInbox when profile == TopologyProfile.LoadTests => ($"{LoadTestSupportBaseUrl}/corebank/inbox", HttpMethod.Get),
        KnownEndpoints.CoreBankOutbox when profile == TopologyProfile.LoadTests => ($"{LoadTestSupportBaseUrl}/corebank/outbox", HttpMethod.Get),
        _ => throw new ArgumentOutOfRangeException(nameof(endpointId), endpointId, "Unknown endpoint."),
    };

    private static string RequirePathParameter(string endpointId, string? pathParameter) =>
        string.IsNullOrWhiteSpace(pathParameter)
            ? throw new ArgumentException($"Endpoint '{endpointId}' requires a path parameter.", nameof(pathParameter))
            : pathParameter;

    public static string LinkFor(string linkId) => linkId switch
    {
        KnownLinks.Jaeger => JaegerLinkUrl,
        _ => throw new ArgumentOutOfRangeException(nameof(linkId), linkId, "Unknown link."),
    };

    public static readonly IReadOnlyDictionary<string, int> RegularProfilePorts = new Dictionary<string, int>
    {
        [KnownResources.PaymentsApi] = 5294,
        [KnownResources.CoreBankApi] = 5032,
        [KnownResources.Jaeger] = 16686,
    };

    public static readonly IReadOnlyDictionary<string, int> LoadTestProfilePorts = new Dictionary<string, int>
    {
        [KnownResources.PaymentsApi] = 5295,
        [KnownResources.LoadTestSupport] = 5181,
        [KnownResources.CoreBankApi] = 5032,
    };

    private static string PaymentsBaseUrl(TopologyProfile profile) =>
        profile == TopologyProfile.LoadTests ? LoadPaymentsApiBaseUrl : RegularPaymentsApiBaseUrl;
}
