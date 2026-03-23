using System.Collections.Immutable;
using CommunityToolkit.Aspire.Hosting.Dapr;

var builder = DistributedApplication.CreateBuilder(args);

string daprComponentsPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "dapr", "components-loadtest"));
string k6ScriptPath       = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "k6"));

// ---------------------------------------------------------------------------
// Disposable Postgres — Aspire creates both databases via AddDatabase();
// EnsureCreated() in each API creates the schema and seeds initial data.
// ---------------------------------------------------------------------------
var postgres = builder.AddPostgres("postgres")
    .WithPgAdmin();

var paymentsDb = postgres.AddDatabase("paymentsdb");
var coreBankDb = postgres.AddDatabase("corebankdb");

// ---------------------------------------------------------------------------
// Disposable Redis — different host port to avoid clashing with the main AppHost.
// Password must match the value hardcoded in dapr/components-loadtest/*.yaml
// ---------------------------------------------------------------------------
var redisPassword = builder.AddParameter("redis-password", "myredispassword123", secret: true);
#pragma warning disable ASPIRECERTIFICATES001
var redis = builder
    .AddRedis("redis", password: redisPassword)
    .WithHostPort(6381)
    .WithEndpointProxySupport(false)
    .WithoutHttpsCertificate()
    .WithImageTag("7.4-alpine");
#pragma warning restore ASPIRECERTIFICATES001

// ---------------------------------------------------------------------------
// Dapr
// ---------------------------------------------------------------------------
builder.AddDapr();

var pubsub = builder.AddDaprPubSub("pubsub", new DaprComponentOptions
{
    LocalPath = Path.Combine(daprComponentsPath, "pubsub-redis.yaml")
}).WaitFor(redis);

var lockStore = builder.AddDaprComponent("lockstore", "lock.redis", new DaprComponentOptions
{
    LocalPath = Path.Combine(daprComponentsPath, "lockstore-redis.yaml")
}).WaitFor(redis);

// ---------------------------------------------------------------------------
// Core Bank API
// ---------------------------------------------------------------------------
var coreBankApi = builder.AddProject<Projects.CoreBankDemo_CoreBankAPI>("corebank-api")
    .WithReference(coreBankDb)
    .WaitFor(coreBankDb)
    .WithHttpHealthCheck("/health")
    .WithDaprSidecar(opt =>
    {
        opt.WithOptions(new DaprSidecarOptions
        {
            AppId = "corebank-api",
            ResourcesPaths = ImmutableHashSet.Create(daprComponentsPath),
            SchedulerHostAddress = "",
            PlacementHostAddress = "",
        });
        opt.WithReference(pubsub);
        opt.WithReference(lockStore);
    })
    .WaitFor(pubsub)
    .WaitFor(lockStore);

// ---------------------------------------------------------------------------
// Payments API
// ---------------------------------------------------------------------------
var paymentsApi = builder.AddProject<Projects.CoreBankDemo_PaymentsAPI>("payments-api")
    .WithReference(paymentsDb)
    .WaitFor(paymentsDb)
    .WithReference(coreBankApi)
    .WithEnvironment("Features__UseDapr", "true")
    .WaitFor(coreBankApi)
    .WithDaprSidecar(opt =>
    {
        opt.WithOptions(new DaprSidecarOptions
        {
            AppId = "payments-api",
            ResourcesPaths = ImmutableHashSet.Create(daprComponentsPath),
            SchedulerHostAddress = "",
            PlacementHostAddress = "",
        });
        opt.WithReference(pubsub);
        opt.WithReference(lockStore);
    });

// ---------------------------------------------------------------------------
// LoadTestSupport API — minimal API for post-run assertions, reads both DBs
// ---------------------------------------------------------------------------
var loadTestSupport = builder.AddProject<Projects.CoreBankDemo_LoadTestSupport>("loadtest-support")
    .WithReference(paymentsDb)
    .WithReference(coreBankDb)
    .WithHttpHealthCheck("/health")
    .WaitFor(coreBankApi)
    .WaitFor(paymentsApi);

// ---------------------------------------------------------------------------
// k6 container
// APIs run on the host, so k6 (in Docker) reaches them via host.docker.internal.
// Ports match launchSettings.json: PaymentsAPI=5294, LoadTestSupport=5180
// Override TRANSACTION_COUNT and VU_COUNT via appsettings.json "LoadTest" section:
//   "LoadTest": { "TransactionCount": "10000", "VuCount": "50" }
// ---------------------------------------------------------------------------
var transactionCount = builder.Configuration["LoadTest:TransactionCount"] ?? "1000";
var vuCount          = builder.Configuration["LoadTest:VuCount"]          ?? "10";

builder.AddContainer("k6", "grafana/k6")
    .WithArgs(
        "run",
        "--env", $"TRANSACTION_COUNT={transactionCount}",
        "--env", $"VU_COUNT={vuCount}",
        "--env", "PAYMENTS_API_URL=http://host.docker.internal:5294",
        "--env", "LOAD_TEST_SUPPORT_URL=http://host.docker.internal:5180",
        "/scripts/script.js")
    .WithBindMount(k6ScriptPath, "/scripts", isReadOnly: true)
    .WaitFor(loadTestSupport)
    .WaitFor(paymentsApi);

builder.Build().Run();
