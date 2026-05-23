using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.LoadTestSupport;
using CoreBankDemo.LoadTestSupport.Endpoints;
using CoreBankDemo.LoadTestSupport.McpTools;
using CoreBankDemo.PaymentsAPI;

namespace CoreBankDemo.LoadTestSupport;

public class Program
{
    public static void Main(params string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.AddServiceDefaults("CoreBank.LoadTestSupport");

// Health checks so Aspire's WaitFor blocks until both schemas are ready
        builder.Services.AddHealthChecks()
            .AddDbContextCheck<CoreBankDbContext>("corebankread-db")
            .AddDbContextCheck<PaymentsDbContext>("paymentsread-db");

// Connect to both databases using the actual DbContexts from the APIs
        builder.AddNpgsqlDbContext<CoreBankDbContext>("corebankdb");
        builder.AddNpgsqlDbContext<PaymentsDbContext>("paymentsdb");

// MCP server — exposes load test tools to AI agents via Streamable HTTP
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<LoadTestTools>();

        var app = builder.Build();

// Seed the 10 load test accounts into the CoreBank database.
// CoreBankAPI (which we wait for) has already run EnsureCreated() and seeded
// the regular accounts, so the schema and table are guaranteed to exist here.
        SeedLoadTestAccounts(app);

        app.MapDefaultEndpoints();
        app.MapMcp();
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

                    var loadTestAccounts = Enumerable.Range(1, LoadTestConstants.AccountCount)
                        .Select(i => new Account
                        {
                            AccountNumber = $"NL{i:D2}LOAD{i:D10}",
                            AccountHolderName = $"Load Test Account {i:D2}",
                            Balance = LoadTestConstants.InitialBalance,
                            Currency = "EUR",
                            IsActive = true,
                            CreatedAt = now
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
    }
}
