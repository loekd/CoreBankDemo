using CoreBankDemo.Messaging.Inbox;
using CoreBankDemo.Messaging.Outbox;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.PaymentsAPI.Controllers;
using CoreBankDemo.PaymentsAPI.Handlers;

var builder = WebApplication.CreateBuilder(args);

// Add Aspire Service Defaults (includes OpenTelemetry, health checks, service discovery)
builder.AddServiceDefaults("CoreBank.PaymentsAPI", new[] { nameof(OutboxProcessor), nameof(TransactionEventHandler), nameof(PaymentsController), nameof(InboxProcessor) });

// Explicit DB health check so Aspire's WaitFor blocks until the schema is ready
builder.Services.AddHealthChecks()
    .AddDbContextCheck<PaymentsDbContext>("payments-db");

// Add configuration options with validation
builder.AddOutboxProcessingOptions();
builder.AddInboxProcessingOptions();

// Add Dapr
builder.Services.AddControllers().AddDapr();
builder.Services.AddDaprClient();
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton(TimeProvider.System);

// Database for Outbox and Inbox patterns
builder.AddNpgsqlDbContext<PaymentsDbContext>("paymentsdb");
builder.Services.AddHttpClient<ICoreBankApiClient, HttpCoreBankApiClient>(client =>
    {
        client.BaseAddress = new Uri("https+http://corebank-api");
    })
    .AddServiceDiscovery();


// Outbox: ensure reliable message delivery to CoreBank API
builder.Services.AddScoped<OutboxMessageRepositoryBase<OutboxMessage, PaymentsDbContext>, OutboxRepository>();
builder.Services.AddScoped<IOutboxRepository, OutboxRepository>();
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.AddScoped<ITransactionEventHandler, TransactionEventHandler>();

// Inbox: de-duplicate incoming transaction events
builder.Services.AddScoped<InboxMessageRepositoryBase<InboxMessage, PaymentsDbContext>, InboxMessageRepository>();
builder.Services.AddScoped<IInboxMessageRepository, InboxMessageRepository>();
builder.Services.AddHostedService<InboxProcessor>();

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