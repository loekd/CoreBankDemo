using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.LoadTestSupport;
using CoreBankDemo.LoadTestSupport.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("CoreBank.LoadTestSupport");

// Health checks so Aspire's WaitFor blocks until both schemas are ready
builder.Services.AddHealthChecks()
    .AddDbContextCheck<CoreBankReadDbContext>("corebankread-db")
    .AddDbContextCheck<PaymentsReadDbContext>("paymentsread-db");

// Connect to both databases (read-only mirror for assertions)
builder.AddNpgsqlDbContext<CoreBankReadDbContext>("corebankdb");
builder.AddNpgsqlDbContext<PaymentsReadDbContext>("paymentsdb");

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
    var db = scope.ServiceProvider.GetRequiredService<CoreBankReadDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

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
