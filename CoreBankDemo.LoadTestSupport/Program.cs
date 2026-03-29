using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.LoadTestSupport.Endpoints;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("CoreBank.LoadTestSupport");

// Health checks so Aspire's WaitFor blocks until both schemas are ready
builder.Services.AddHealthChecks()
    .AddDbContextCheck<CoreBankDbContext>("corebankread-db")
    .AddDbContextCheck<PaymentsDbContext>("paymentsread-db")
    .AddCheck("corebank-schema", () =>
    {
        // Check will run after InitializeDatabaseWithSeedAccounts() completes
        // This ensures dependent services only start after schema is ready
#pragma warning disable ASP0000
        using var scope = builder.Services.BuildServiceProvider().CreateScope();
#pragma warning restore ASP0000
        var db = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();

        // Verify critical tables exist by attempting a simple query
        var accountExists = false;
        try
        {
            accountExists = db.Accounts.Any();
        }
        catch
        {
            //still loading
            Thread.Sleep(200);
        }

        return accountExists ? 
            HealthCheckResult.Healthy("Schema initialized")
            : new HealthCheckResult(HealthStatus.Unhealthy, "Schema not initialized");
    });

// Connect to both databases using the actual DbContexts from the APIs
builder.AddNpgsqlDbContext<CoreBankDbContext>("corebankdb");
builder.AddNpgsqlDbContext<PaymentsDbContext>("paymentsdb");

var app = builder.Build();

// Seed the 10 load test accounts into the CoreBank database.
// CoreBankAPI (which we wait for) has already run EnsureCreated() and seeded
// the regular accounts, so the schema and table are guaranteed to exist here.
SeedLoadTestAccounts(app);

app.MapDefaultEndpoints();
app.MapResetEndpoints();
app.MapAssertEndpoints();
app.MapInboxEndpoints();
app.MapOutboxEndpoints();

app.Run();

static void SeedLoadTestAccounts(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // CoreBankAPI has already initialized the schema (verified by health check in AppHost)
    var strategy = db.Database.CreateExecutionStrategy();
    strategy.Execute(
        state: (db, logger),
        operation: (context, state) =>
        {
            var (dbContext, log) = state;
            var now = TimeProvider.System.GetUtcNow().UtcDateTime;

            var loadTestAccounts = Enumerable.Range(1, 10)
                .Select(i => new Account
                {
                    AccountNumber     = $"NL{i:D2}LOAD{i:D10}",
                    AccountHolderName = $"Load Test Account {i:D2}",
                    Balance           = 10_000_000.00m,
                    Currency          = "EUR",
                    IsActive          = true,
                    CreatedAt         = now
                })
                .ToList();

            // Only insert accounts that don't already exist (idempotent)
            var existing = dbContext.Accounts
                .Where(a => a.AccountNumber.StartsWith("NL") && a.AccountNumber.Contains("LOAD"))
                .Select(a => a.AccountNumber)
                .ToHashSet();

            var toInsert = loadTestAccounts.Where(a => !existing.Contains(a.AccountNumber)).ToList();
            if (toInsert.Count == 0)
            {
                log.LogInformation("Load test accounts already seeded");
                return (object?)null;
            }

            dbContext.Accounts.AddRange(toInsert);
            dbContext.SaveChanges();
            log.LogInformation("Seeded {Count} load test accounts", toInsert.Count);
            return (object?)null;
        },
        verifySucceeded: null
    );
}
