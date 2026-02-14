using Microsoft.EntityFrameworkCore;
using CoreBankDemo.PaymentsAPI;

var builder = WebApplication.CreateBuilder(args);

// Add Aspire Service Defaults (includes OpenTelemetry, health checks, service discovery)
builder.AddServiceDefaults();

// Add Dapr
builder.Services.AddControllers().AddDapr();
builder.Services.AddDaprClient();
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add TimeProvider
builder.Services.AddSingleton(TimeProvider.System);

// Database for Outbox pattern
builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseSqlite("Data Source=payments.db"));

// Resilience (still using standard resilience handler for retry/circuit breaker)
builder.Services.AddHttpClient("CoreBank")
    .AddStandardResilienceHandler();

// Outbox processor (controlled by feature flag)
var useOutbox = builder.Configuration.GetValue<bool>("Features:UseOutbox");
if (useOutbox)
{
    builder.Services.AddHostedService<OutboxProcessor>();
}

var app = builder.Build();

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
    db.Database.EnsureCreated();
}

// Map default endpoints (health checks, etc.)
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.Run();