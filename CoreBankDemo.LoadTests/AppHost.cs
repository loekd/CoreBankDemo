using System.Collections.Immutable;
using CommunityToolkit.Aspire.Hosting.Dapr;
using DevProxy.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var daprComponentsPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "dapr", "components-loadtest"));
var k6ScriptPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "k6"));

var jaeger = builder.AddContainer("jaeger", "jaegertracing/all-in-one", "1.66.0")
    .WithHttpEndpoint(port: 16686, targetPort: 16686, name: "jaeger-ui")
    .WithEndpoint(port: 4317, targetPort: 4317, name: "otlp-grpc")
    .WithEndpoint(port: 4318, targetPort: 4318, name: "otlp-http")
    .WithEnvironment("COLLECTOR_OTLP_ENABLED", "true")
    .WithEndpointProxySupport(false);
var jaegerOtlpGrpcEndpoint = jaeger.GetEndpoint("otlp-grpc");

var postgresPassword = builder.AddParameter("postgres-password", "postgres-dev-load-test", secret: false);
var postgres = builder.AddPostgres("postgres", password: postgresPassword)
    .WithImageTag("18.3");
var paymentsDb = postgres.AddDatabase("paymentsdb");
var coreBankDb = postgres.AddDatabase("corebankdb");

var redisPassword = builder.AddParameter("redis-password", "myredispassword123", secret: false);
#pragma warning disable ASPIRECERTIFICATES001
var redis = builder.AddRedis("redis", password: redisPassword)
    .WithHostPort(6381)
    .WithEndpointProxySupport(false)
    .WithoutHttpsCertificate()
    .WithImageTag("7.4-alpine")
    .WithEnvironment("REDIS_PASSWORD", redisPassword);
#pragma warning restore ASPIRECERTIFICATES001

builder.AddDapr();
var pubsub = builder.AddDaprPubSub("pubsub", new DaprComponentOptions
{
    LocalPath = Path.Combine(daprComponentsPath, "pubsub-redis.yaml")
}).WaitFor(redis);

var coreBankApi = builder.AddProject<Projects.CoreBankDemo_CoreBankAPI>("corebank-api", launchProfileName: "loadtest")
    .WithReplicas(2)
    .WithReference(coreBankDb)
    .WaitFor(coreBankDb)
    .WithReference(redis)
    .WaitFor(redis)
    .WithEnvironment("ProcessorStartGate__Enabled", "true")
    .WithEnvironment("JAEGER_OTLP_ENDPOINT", jaegerOtlpGrpcEndpoint)
    .WithHttpHealthCheck("/health")
    .WithDaprSidecar(options =>
    {
        options.WithOptions(new DaprSidecarOptions
        {
            AppId = "corebank-api",
            ResourcesPaths = ImmutableHashSet.Create(daprComponentsPath),
            SchedulerHostAddress = "",
            PlacementHostAddress = "",
            EnableApiLogging = true,
            Config = Path.Combine(daprComponentsPath, "otel-config.yaml")
        });
        options.WithReference(pubsub);
    })
    .WaitFor(jaeger)
    .WaitFor(pubsub);

// Opt-in latency injection (Features:UseDevProxy, default false). The profile
// delays only CoreBankAPI's transaction endpoint, by more than the instant
// rail's BudgetMilliseconds, so every inline forward attempt overruns its
// budget and the payment falls back to 202 Pending for the background
// processor to complete. Account validation stays fast, so the run still
// exercises the normal path up to the forward. Deliberately off by default --
// this is a manual stress scenario, never part of the /run-load-tests gate.
IResourceBuilder<DevProxyExecutableResource>? devProxy = null;
var useDevProxy = builder.Configuration.GetValue<bool>("Features:UseDevProxy");
if (useDevProxy)
{
    var devProxyConfigFile = Path.Combine(
        builder.AppHostDirectory, "devproxy", "config", "devproxyrc-latency.json");
    devProxy = builder.AddDevProxyExecutable("devproxy")
        .WithConfigFile(devProxyConfigFile)
        .WithUrlsToWatch(() => ["http://127.0.0.1:5032/*"]);
}

var paymentsApi = builder.AddProject<Projects.CoreBankDemo_PaymentsAPI>("payments-api", launchProfileName: "loadtest")
    .WithReplicas(2)
    .WithReference(paymentsDb)
    .WaitFor(paymentsDb)
    .WithReference(redis)
    .WaitFor(redis)
    .WithReference(coreBankApi)
    .WaitFor(coreBankApi)
    .WithEnvironment("ProcessorStartGate__Enabled", "true")
    .WithEnvironment("JAEGER_OTLP_ENDPOINT", jaegerOtlpGrpcEndpoint)
    .WithExternalHttpEndpoints()
    .WithEndpoint("http", endpoint => endpoint.Port = 5295)
    .WithHttpHealthCheck("/health")
    .WithDaprSidecar(options =>
    {
        options.WithOptions(new DaprSidecarOptions
        {
            AppId = "payments-api",
            ResourcesPaths = ImmutableHashSet.Create(daprComponentsPath),
            SchedulerHostAddress = "",
            PlacementHostAddress = "",
            EnableApiLogging = true,
            Config = Path.Combine(daprComponentsPath, "otel-config.yaml")
        });
        options.WithReference(pubsub);
    })
    .WaitFor(jaeger)
    .WaitFor(pubsub);

if (devProxy is not null)
{
    const string devProxyUrl = "http://127.0.0.1:8001";
    // Exclude the Dapr sidecar's pub/sub gRPC port (localhost:50001) from
    // proxying; the Kiota HTTP call to CoreBankAPI is unaffected and still
    // proxied.
    const string noProxy = "localhost";

    // Both casings, deliberately -- .NET's HttpEnvironmentProxy reads the
    // lowercase names first and only falls back to the uppercase ones, so an
    // inherited lowercase http_proxy (every dev container and sandbox exports
    // one) would otherwise win and route CoreBankAPI calls to that outer proxy
    // instead. Same reasoning as CoreBankDemo.AppHost/AppHost.cs.
    paymentsApi
        .WithEnvironment("HTTP_PROXY", devProxyUrl)
        .WithEnvironment("HTTPS_PROXY", devProxyUrl)
        .WithEnvironment("NO_PROXY", noProxy)
        .WithEnvironment("http_proxy", devProxyUrl)
        .WithEnvironment("https_proxy", devProxyUrl)
        .WithEnvironment("no_proxy", noProxy)
        .WaitFor(devProxy);
}

var loadTestSupport = builder.AddProject<Projects.CoreBankDemo_LoadTestSupport>("loadtest-support", launchProfileName: "loadtest")
    .WithReference(paymentsDb)
    .WithReference(coreBankDb)
    .WithReference(redis)
    .WithEnvironment("ProcessorStartGate__Enabled", "true")
    .WithEnvironment("ProcessorStartGate__ExpectedParticipants", "4")
    .WithExternalHttpEndpoints()
    .WithHttpEndpoint(name: "load-test", port: 5181)
    .WithHttpHealthCheck("/health")
    .WaitFor(coreBankApi)
    .WaitFor(paymentsApi)
    .WaitFor(redis);

var initializer = builder.AddProject<Projects.CoreBankDemo_LoadTestInitializer>("loadtest-initializer")
    .WithReference(loadTestSupport)
    .WaitFor(coreBankApi)
    .WaitFor(paymentsApi)
    .WaitFor(loadTestSupport);

var transactionCount = builder.Configuration["LoadTest:TransactionCount"] ?? "100";
var vuCount = builder.Configuration["LoadTest:VuCount"] ?? "10";

builder.AddContainer("k6", "grafana/k6")
    .WithArgs("run", "/scripts/script.js")
    .WithEnvironment("TRANSACTION_COUNT", transactionCount)
    .WithEnvironment("VU_COUNT", vuCount)
    .WithEnvironment("PAYMENTS_API_URL", paymentsApi.GetEndpoint("http"))
    .WithEnvironment("LOAD_TEST_SUPPORT_URL", loadTestSupport.GetEndpoint("load-test"))
    .WithBindMount(k6ScriptPath, "/scripts", isReadOnly: true)
    .WaitForCompletion(initializer);

builder.Build().Run();
