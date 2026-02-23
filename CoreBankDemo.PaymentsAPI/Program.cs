using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.PaymentsAPI.Controllers;
using CoreBankDemo.PaymentsAPI.Handlers;

var builder = WebApplication.CreateBuilder(args);

// Add Aspire Service Defaults (includes OpenTelemetry, health checks, service discovery)
builder.AddServiceDefaults("CoreBank.PaymentsAPI", new[] { nameof(OutboxProcessor), nameof(TransactionEventHandler), nameof(PaymentsController) });

// Add configuration options with validation
builder.AddOutboxProcessingOptions();

// Add Dapr
builder.Services.AddControllers().AddDapr();
builder.Services.AddDaprClient();
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add TimeProvider
builder.Services.AddSingleton(TimeProvider.System);

// Database for Outbox pattern
builder.AddNpgsqlDbContext<PaymentsDbContext>("paymentsdb");

// Outbox: select Core Bank API transport based on feature flag
var useDapr = builder.Configuration.GetValue<bool>("Features:UseDapr");
if (useDapr)
{
    builder.Services.AddSingleton<ICoreBankApiClient, DaprCoreBankApiClient>();
}
else
{
    builder.Services.AddHttpClient<ICoreBankApiClient, HttpCoreBankApiClient>(client =>
        {
            client.BaseAddress = new Uri("https+http://corebank-api");
        })
        .AddServiceDiscovery()
        .AddStandardResilienceHandler();
}

builder.Services.AddSingleton<IOutboxMessageHandler, OutboxMessageHandler>();

// Outbox processor (controlled by feature flag)
var useOutbox = builder.Configuration.GetValue<bool>("Features:UseOutbox");
if (useOutbox)
{
    builder.Services.AddHostedService<OutboxProcessor>();
}

builder.Services.AddScoped<ITransactionEventHandler, TransactionEventHandler>();

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

app.UseCloudEvents();

app.MapSubscribeHandler();

app.MapControllers();

app.Run();