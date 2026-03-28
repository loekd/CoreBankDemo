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
    .WithEndpointProxySupport(false);

// Resolve the host-visible Jaeger OTLP endpoint from Aspire.
// This avoids hardcoding localhost:4317, which can be remapped to a dynamic host port.
var jaegerOtlpGrpcEndpoint = jaeger.GetEndpoint("otlp-grpc");

// Add PostgreSQL for Payments API and Core Bank API
var postgres = builder.AddPostgres("postgres")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithPgAdmin();

var paymentsDb = postgres.AddDatabase("paymentsdb");
var coreBankDb = postgres.AddDatabase("corebankdb");

// Add Redis for Dapr components (pub/sub + lock store)
// Use a parameter with default value so Dapr YAML can use the same password
var redisPassword = builder.AddParameter("redis-password", secret: false);
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

// Add Dapr lock store component (Redis-backed)
var lockStore = builder.AddDaprComponent("lockstore", "lock.redis", new DaprComponentOptions
{
    LocalPath = Path.Combine(daprComponentsPath, "lockstore-redis.yaml")
}).WaitFor(redis);

// Core Bank API (Legacy System) with Dapr sidecar
// Ports are defined in launchSettings.json (5032)
// Runs at 127.0.0.1 instead of localhost, so it will be proxied.
var coreBankApi = builder.AddProject<Projects.CoreBankDemo_CoreBankAPI>("corebank-api")
    .WithReference(coreBankDb)
    .WaitFor(coreBankDb)
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
        opt.WithReference(lockStore);
    })
    .WithUrl("/swagger", "Swagger UI")
    .WaitFor(jaeger)
    .WaitFor(pubsub)
    .WaitFor(lockStore);

// Payments API (Main Service) with Dapr sidecar
// Ports are defined in launchSettings.json (5294)
IResourceBuilder<DevProxyExecutableResource>? devProxy = null;
var useDevProxy = builder.Configuration.GetValue<bool>("Features:UseDevProxy");
if (useDevProxy)
{
    var devProxyConfigFolder = Path.Combine(builder.AppHostDirectory, "devproxy", "config");
    var devProxyConfigFile = Path.Combine(devProxyConfigFolder, "devproxyrc.json");
    devProxy = builder.AddDevProxyExecutable("devproxy")
        .WithConfigFile(devProxyConfigFile)
        .WithUrlsToWatch(() => ["http://127.0.0.1:5032/*"]); // Watch the Core Bank API URL for availability

}

var paymentsApi = builder.AddProject<Projects.CoreBankDemo_PaymentsAPI>("payments-api")
    .WithReference(paymentsDb)
    .WaitFor(paymentsDb)
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
        opt.WithReference(lockStore);
    });

if (devProxy is not null)
{
    paymentsApi
        .WithReference(coreBankApi)
        .WithEnvironment("Features__UseDapr", "false")  //override any other config because Dapr sidecar circumvents proxy
        .WithEnvironment("HTTP_PROXY", devProxy.GetEndpoint(DevProxyResource.ProxyEndpointName))
        .WithEnvironment("HTTPS_PROXY", devProxy.GetEndpoint(DevProxyResource.ProxyEndpointName))
        .WithEnvironment("NO_PROXY", "localhost") // Exclude Dapr sidecar gRPC (localhost:50001) from proxy
        .WaitFor(devProxy);
}
else
{
    paymentsApi
        .WithReference(coreBankApi)    
        .WithEnvironment("Features__UseDapr", "true");
}

builder.Build().Run();
