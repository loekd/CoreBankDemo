using System.Collections.Immutable;
using CommunityToolkit.Aspire.Hosting.Dapr;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

string daprComponentsPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "dapr", "components"));

// Add Jaeger for distributed tracing
var jaeger = builder.AddContainer("jaeger", "jaegertracing/all-in-one", "1.66.0")
    .WithHttpEndpoint(port: 16686, targetPort: 16686, name: "jaeger-ui")
    .WithEndpoint(port: 4317, targetPort: 4317, name: "otlp-grpc")
    .WithEndpoint(port: 4318, targetPort: 4318, name: "otlp-http")
    .WithEnvironment("COLLECTOR_OTLP_ENABLED", "true");

// APIs and Dapr sidecars run on the host, not in Docker, so use localhost (port 4317 is mapped from the Jaeger container)
var jaegerOtlpGrpcEndpoint = "http://localhost:4317";

// Add Redis for Dapr components (pub/sub + lock store)
// Use a parameter with default value so Dapr YAML can use the same password
var redisPassword = builder.AddParameter("redis-password", secret: false);
#pragma warning disable ASPIRECERTIFICATES001
var redis = builder
    .AddRedis("redis", password: redisPassword)
    .WithHostPort(6380)
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
var coreBankApi = builder.AddProject<Projects.CoreBankDemo_CoreBankAPI>("corebank-api")
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
var paymentsApi = builder.AddProject<Projects.CoreBankDemo_PaymentsAPI>("payments-api")
    .WithEnvironment("JAEGER_OTLP_ENDPOINT", jaegerOtlpGrpcEndpoint)
    .WithReference(coreBankApi)
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
    })
    .WithUrl("/swagger", "Swagger UI")
    .WaitFor(coreBankApi);

builder.Build().Run();
