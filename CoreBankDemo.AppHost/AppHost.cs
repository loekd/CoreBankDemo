using System.Collections.Immutable;
using CommunityToolkit.Aspire.Hosting.Dapr;
using DevProxy.Hosting;
using Microsoft.Extensions.Configuration;


var builder = DistributedApplication.CreateBuilder(args);

string daprComponentsPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "dapr", "components"));

// Add Jaeger for distributed tracing
var jaeger = builder.AddContainer("jaeger", "jaegertracing/all-in-one", "1.66.0")
    .WithHttpEndpoint(port: 16686, targetPort: 16686, name: "jaeger-ui")
    .WithEndpoint(port: 4317, targetPort: 4317, name: "otlp-grpc")
    .WithEndpoint(port: 4318, targetPort: 4318, name: "otlp-http")
    .WithEnvironment("COLLECTOR_OTLP_ENABLED", "true")
    .WithEndpointProxySupport(false)
    // Publish the Jaeger UI on every interface, not just loopback. Aspire binds a
    // container's host ports to `TargetHost`, which defaults to "localhost", so the
    // UI answered only on 127.0.0.1. That is invisible when the AppHost runs in a
    // devcontainer -- the editor forwards ports from the inside, where loopback is
    // fine -- and fatal when it runs directly in a sandbox, where the only way out
    // is a host-side port publish that has to reach the sandbox's eth0. The OTLP
    // endpoints below are deliberately left alone: only in-sandbox services dial
    // them, and their resolved URL is handed to those services verbatim.
    .WithEndpoint("jaeger-ui", endpoint => endpoint.TargetHost = "0.0.0.0")
    .WithLifetime(ContainerLifetime.Persistent);

// Resolve the host-visible Jaeger OTLP endpoint from Aspire.
// This avoids hardcoding localhost:4317, which can be remapped to a dynamic host port.
var jaegerOtlpGrpcEndpoint = jaeger.GetEndpoint("otlp-grpc");

// Add PostgreSQL for Payments API and Core Bank API with fixed connection string and persistent lifetime
var postgresPassword = builder.AddParameter("postgres-password", "postgres-dev-load-test", secret: false);
var postgres = builder.AddPostgres("postgres", password: postgresPassword, port: 5432)
    // Story 6.6 (ADR-016): the PostgreSQL major version is pinned explicitly
    // rather than inherited from Aspire's implicit default, and must stay in
    // lockstep with the persistence integration tier's pinned image
    // (tests/CoreBankDemo.Persistence.IntegrationTests/Infrastructure/PostgresImage.cs).
    .WithImageTag("18.3")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithPgAdmin();

var paymentsDb = postgres.AddDatabase("paymentsdb");
var coreBankDb = postgres.AddDatabase("corebankdb");

// Add Redis for Dapr pub/sub and direct distributed locking
// Use a parameter with default value so Dapr YAML can use the same password
var redisPassword = builder.AddParameter("redis-password", "myredispassword123", secret: false);
#pragma warning disable ASPIRECERTIFICATES001
var redis = builder
    .AddRedis("redis", password: redisPassword)
    .WithHostPort(6379)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEndpointProxySupport(false)
    .WithoutHttpsCertificate()
    .WithRedisInsight(opt => opt.WithoutHttpsCertificate())
    .WithImageTag("7.4-alpine")
    .WithEnvironment("REDIS_PASSWORD", redisPassword);
#pragma warning restore ASPIRECERTIFICATES001

// Add Dapr
builder.AddDapr();

// Add Dapr pub/sub component (Redis-backed)
var pubsub = builder.AddDaprPubSub("pubsub", new DaprComponentOptions
{
    LocalPath = Path.Combine(daprComponentsPath, "pubsub-redis.yaml")
}).WaitFor(redis);

// Story 6.2 (ADR-011): partition locking now goes directly through
// DistributedLock.Redis over the shared "redis" resource below — no Dapr
// lock component. Dapr remains the pub/sub adapter only.

// Core Bank API (Legacy System) with Dapr sidecar
// Ports are defined in launchSettings.json (5032)
// Runs at 127.0.0.1 instead of localhost, so it will be proxied.
var coreBankApi = builder.AddProject<Projects.CoreBankDemo_CoreBankAPI>("corebank-api")
    .WithReplicas(2)
    .WithReference(coreBankDb)
    .WaitFor(coreBankDb)
    .WithReference(redis)
    .WaitFor(redis)
    .WithHttpHealthCheck("/health")
    .WithEnvironment("JAEGER_OTLP_ENDPOINT", jaegerOtlpGrpcEndpoint)
    .WithDaprSidecar(opt =>
    {
        opt.WithOptions(new DaprSidecarOptions
        {
            AppId = "corebank-api",
            ResourcesPaths = ImmutableHashSet.Create(daprComponentsPath),
            SchedulerHostAddress = "", // Disable Dapr scheduler
            PlacementHostAddress = "", // Disable Dapr placement
            EnableApiLogging = true,
            // Configure Dapr sidecar to send telemetry to Jaeger
            Config = Path.Combine(daprComponentsPath, "otel-config.yaml"),
        });
        opt.WithReference(pubsub);
    })
    .WithUrl("/swagger", "Swagger UI")
    .WaitFor(jaeger)
    .WaitFor(pubsub);

// Payments API (Main Service) with Dapr sidecar
// Ports are defined in launchSettings.json (5294)
IResourceBuilder<DevProxyExecutableResource>? devProxy = null;
var useDevProxy = builder.Configuration.GetValue<bool>("Features:UseDevProxy");
if (useDevProxy)
{
    var devProxyConfigFolder = Path.Combine(builder.AppHostDirectory, "devproxy", "config");
    // DemoRunner's Faults workspace steers levels by writing a gitignored session config
    // beside the checked-in profile, which Dev Proxy then reloads on its own. Prefer it
    // when present; the checked-in file stays a read-only preset source either way.
    var generatedConfigFile = Path.Combine(devProxyConfigFolder, "generated", "devproxyrc.session.json");
    var devProxyConfigFile = File.Exists(generatedConfigFile)
        ? generatedConfigFile
        : Path.Combine(devProxyConfigFolder, "devproxyrc.json");
    devProxy = builder.AddDevProxyExecutable("devproxy")
        .WithConfigFile(devProxyConfigFile)
        // Dev Proxy 3.2.0's restart-on-config-change is broken: after "Configuration file
        // changed. Restarting proxy..." it accepts TCP connections and immediately closes
        // them, and never serves again. DemoRunner therefore restarts this resource itself
        // after writing a new session config (ADR-019); the watcher is dead weight that can
        // only take the proxy down. Belt-and-braces only -- the console's atomic
        // temp-then-move write already avoids firing the watcher -- so this line is safe to
        // drop if it ever conflicts with how DevProxy.Hosting composes its own arguments.
        .WithArgs("--no-watch")
        .WithUrlsToWatch(() => ["http://127.0.0.1:5032/*"]); // Watch the Core Bank API URL for availability

}

var paymentsApi = builder.AddProject<Projects.CoreBankDemo_PaymentsAPI>("payments-api")
    .WithReplicas(2)
    .WithReference(paymentsDb)
    .WaitFor(paymentsDb)
    .WithReference(redis)
    .WaitFor(redis)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithEnvironment("JAEGER_OTLP_ENDPOINT", jaegerOtlpGrpcEndpoint)
    .WithUrl("/swagger", "Swagger UI")
    .WaitFor(coreBankApi)
    .WithDaprSidecar(opt =>
    {
        opt.WithOptions(new DaprSidecarOptions
        {
            AppId = "payments-api",
            ResourcesPaths = ImmutableHashSet.Create(daprComponentsPath),
            SchedulerHostAddress = "", // Disable Dapr scheduler
            PlacementHostAddress = "", // Disable Dapr placement
            EnableApiLogging = true,
            // Configure Dapr sidecar to send telemetry to Jaeger
            Config = Path.Combine(daprComponentsPath, "otel-config.yaml"),
        });
        opt.WithReference(pubsub);
    });

// Story 6.7 (ADR-008): PaymentsAPI always reaches CoreBankAPI through the
// single Kiota-backed HTTP client over the logical "corebank-api" endpoint --
// there is no transport selector. DevProxy only changes whether that same
// HTTP request path is additionally routed through the proxy for fault
// injection; it never switches to a different client or transport.
paymentsApi.WithReference(coreBankApi);

if (devProxy is not null)
{
    const string devProxyUrl = "http://127.0.0.1:8000";
    // Exclude the Dapr sidecar's pub/sub gRPC port (localhost:50001) from
    // proxying; the Kiota HTTP call to CoreBankAPI is unaffected and still
    // proxied.
    const string noProxy = "localhost";

    // Both casings, deliberately. .NET's HttpEnvironmentProxy reads the
    // lowercase names *first* and only falls back to the uppercase ones, so
    // in any environment that already exports a lowercase http_proxy -- every
    // dev container, sandbox, and corporate-proxy setup does -- the inherited
    // value silently wins over the uppercase pair below. PaymentsAPI then
    // bypasses Dev Proxy entirely and sends its CoreBankAPI calls to that
    // outer proxy, which answers 403 for a loopback address it has no rule
    // for; account validation fails, the outbox row exhausts its retries, and
    // the payment never settles. Setting both casings makes the value chosen
    // here the value that actually applies.
    paymentsApi
        .WithEnvironment("HTTP_PROXY", devProxyUrl)
        .WithEnvironment("HTTPS_PROXY", devProxyUrl)
        .WithEnvironment("NO_PROXY", noProxy)
        .WithEnvironment("http_proxy", devProxyUrl)
        .WithEnvironment("https_proxy", devProxyUrl)
        .WithEnvironment("no_proxy", noProxy)
        .WaitFor(devProxy);
}

builder.Build().Run();
