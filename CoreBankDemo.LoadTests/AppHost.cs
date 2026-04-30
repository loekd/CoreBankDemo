using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

string k6ScriptPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "k6"));

// ---------------------------------------------------------------------------
// Connect to the persistent Postgres from the regular AppHost using fixed connection strings
// ---------------------------------------------------------------------------
var postgresPassword = "postgres-dev-load-test";
var paymentsConnectionString = $"Host=localhost;Port=5432;Username=postgres;Password={postgresPassword};Database=paymentsdb";
var coreBankConnectionString = $"Host=localhost;Port=5432;Username=postgres;Password={postgresPassword};Database=corebankdb";

// ---------------------------------------------------------------------------
// LoadTestSupport API — minimal API for post-run assertions, reads both DBs
// ---------------------------------------------------------------------------
var loadTestSupport = builder.AddProject<Projects.CoreBankDemo_LoadTestSupport>("loadtest-support")
    .WithEnvironment("ConnectionStrings__paymentsdb", paymentsConnectionString)
    .WithEnvironment("ConnectionStrings__corebankdb", coreBankConnectionString)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithHttpEndpoint(name: "load-test", port: 5181);

// ---------------------------------------------------------------------------
// k6 container
// The regular AppHost exposes payments-api on port 5295, so k6 targets that.
// Override TRANSACTION_COUNT and VU_COUNT via appsettings.json "LoadTest" section:
//   "LoadTest": { "TransactionCount": "10000", "VuCount": "50" }
// ---------------------------------------------------------------------------
var transactionCount = builder.Configuration["LoadTest:TransactionCount"] ?? "100";
var vuCount          = builder.Configuration["LoadTest:VuCount"]          ?? "10";

builder.AddContainer("k6", "grafana/k6")
    .WithArgs(
        "run",
        "--env", $"TRANSACTION_COUNT={transactionCount}",
        "--env", $"VU_COUNT={vuCount}",
        "--env", "PAYMENTS_API_URL=http://host.docker.internal:5295",
        "--env", "LOAD_TEST_SUPPORT_URL=http://host.docker.internal:5181",
        "/scripts/script.js")
    .WithBindMount(k6ScriptPath, "/scripts", isReadOnly: true)
    .WaitFor(loadTestSupport);

builder.Build().Run();
