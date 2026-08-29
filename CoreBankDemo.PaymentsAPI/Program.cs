using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.PaymentsAPI.Outbox;
var builder = WebApplication.CreateBuilder(args);

// Story 6.2 (ADR-011): register Aspire's Redis client for the shared "redis"
// resource before AddServiceDefaults, so IDistributedLockService resolves to
// RedisDistributedLockService rather than the no-op fallback.
builder.AddRedisClient("redis");

builder.AddServiceDefaults("CoreBank.PaymentsAPI");

builder.Services.AddHealthChecks()
    .AddDbContextCheck<PaymentsDbContext>("payments-db");

builder.AddNpgsqlDbContext<PaymentsDbContext>("paymentsdb");
builder.Services.AddPaymentStorage(builder.Configuration);
builder.Services.AddCoreBankApiClient();

// Story 5.4: forwarding processor. IOutboxRepository is already scoped by
// AddPaymentStorage; expose the same instance under the kernel's narrow
// IOutboxMessageStore<TMessage> port too, so PaymentsOutboxProcessor never
// depends on OutboxRepository directly (messaging-patterns skill).
builder.Services.AddScoped<IOutboxMessageStore<OutboxMessage>>(
    sp => sp.GetRequiredService<OutboxRepository>());
builder.Services.AddScoped<IOutboxDeliveryStrategy<OutboxMessage>, HttpForwardOutboxDeliveryStrategy>();
builder.Services.AddHostedService<PaymentsOutboxProcessor>();

builder.Services.AddPaymentIntake();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapDefaultEndpoints();
app.MapPaymentIntake();

app.Run();
