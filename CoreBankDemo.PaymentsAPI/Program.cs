using CoreBankDemo.PaymentsAPI;
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
