using CoreBankDemo.CoreBankAPI;

var builder = WebApplication.CreateBuilder(args);

// Dapr must be registered before AddServiceDefaults(): ServiceDefaults only
// registers IEventPublisher when a DaprClient is already present in DI at
// that point (epic 3's retrospective flagged the reverse order as a silent
// registration failure). Nothing in this story consumes IEventPublisher yet
// (that's story 4.7), but the ordering is set correctly here so later
// stories don't inherit the landmine.
builder.Services.AddDaprClient();

builder.AddServiceDefaults("CoreBank.CoreBankAPI");

builder.Services.AddSingleton(TimeProvider.System);

// Database for the ledger, inbox, and outbox tables (connection string name
// matches the legacy service).
builder.AddNpgsqlDbContext<CoreBankDbContext>("corebankdb");

builder.Services.AddScoped<DemoAccountSeeder>();

var app = builder.Build();

// Ensure schema exists and demo accounts are seeded (idempotent — safe on
// every startup). No EF migrations, ever (this repo's convention).
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();
    await dbContext.Database.EnsureCreatedAsync();

    var seeder = scope.ServiceProvider.GetRequiredService<DemoAccountSeeder>();
    await seeder.SeedAsync();
}

app.MapDefaultEndpoints();

app.Run();
