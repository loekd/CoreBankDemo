using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Dapr must be registered before AddServiceDefaults(): ServiceDefaults only
// registers IEventPublisher when a DaprClient is already present in DI at
// that point (epic 3's retrospective flagged the reverse order as a silent
// registration failure). The messaging outbox delivery strategy registered
// below consumes IEventPublisher through this ordering-sensitive registration.
builder.Services.AddDaprClient();

// Story 6.2 (ADR-011): register Aspire's Redis client for the shared "redis"
// resource before AddServiceDefaults, so IDistributedLockService resolves to
// RedisDistributedLockService rather than the no-op fallback.
builder.AddRedisClient("redis");

builder.AddServiceDefaults("CoreBank.CoreBankAPI");

builder.Services.AddSingleton(TimeProvider.System);

// Database for the ledger, inbox, and outbox tables (connection string name
// matches the legacy service).
builder.AddNpgsqlDbContext<CoreBankDbContext>("corebankdb");

builder.Services.AddScoped<DemoAccountSeeder>();

// Transaction intake (story 4.4): controllers, the manual-ModelState fix
// (see below), inbox partitioning options, and the intake port/handler pair.
builder.Services.AddControllers();

// [ApiController]'s default automatic-400 behavior would otherwise return a
// framework ValidationProblemDetails shape and short-circuit before the
// controller action ever runs, making its manual ModelState.IsValid check
// unreachable dead code (legacy's brownfield defect — spec-4-4's fix).
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.SuppressModelStateInvalidFilter = true;
});

builder.AddInboxProcessingOptions();
builder.AddMessagingOutboxProcessingOptions();

// The ActivitySource is already registered as a singleton by
// AddServiceDefaults("CoreBank.CoreBankAPI") above and wired into the OTel
// trace provider under that same name — reuse it rather than registering a
// second, differently-named ActivitySource, which would silently produce
// spans OpenTelemetry never exports.
builder.Services.AddScoped<InboxMessageRepository>();
builder.Services.AddScoped<IInboxMessageRepository>(sp => sp.GetRequiredService<InboxMessageRepository>());
builder.Services.AddScoped<IInboxMessageStore<InboxMessage>>(sp => sp.GetRequiredService<InboxMessageRepository>());
builder.Services.AddScoped<ITransactionIntakeHandler, TransactionIntakeHandler>();
builder.Services.AddScoped<ITransactionExecutor, TransactionExecutor>();
builder.Services.AddScoped<IOutboxEventEnqueuer, OutboxEventEnqueuer>();
builder.Services.AddScoped<IInboxMessageHandler<InboxMessage>, TransactionExecutionHandler>();
builder.Services.AddHostedService<InboxProcessor>();
builder.Services.AddScoped<MessagingOutboxRepository>();
builder.Services.AddScoped<IOutboxMessageStore<MessagingOutboxMessage>>(
    sp => sp.GetRequiredService<MessagingOutboxRepository>());
builder.Services.AddScoped<IOutboxDeliveryStrategy<MessagingOutboxMessage>, DaprOutboxDeliveryStrategy>();
builder.Services.AddHostedService<MessagingOutboxProcessor>();

// Account read surface (story 4.5): IAccountRepository was built in story 4.3
// but never registered in DI until now (only this story's controller-facing
// wiring was deferred, not the repository's own registration).
builder.Services.AddScoped<IAccountRepository, AccountRepository>();
builder.Services.AddScoped<IAccountQueryHandler, AccountQueryHandler>();

var app = builder.Build();

// Ensure schema exists and demo accounts are seeded (idempotent — safe on
// every startup). No EF migrations, ever (this repo's convention).
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();
    var seeder = scope.ServiceProvider.GetRequiredService<DemoAccountSeeder>();
    await CoreBankDatabaseInitializer.InitializeAsync(dbContext, seeder);
}

app.MapDefaultEndpoints();
app.MapControllers();

app.Run();
