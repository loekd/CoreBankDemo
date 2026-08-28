using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("CoreBank.PaymentsAPI");
builder.AddOutboxProcessingOptions();
builder.Services.AddOptions<OutboxProcessingOptions>()
    .Validate(options => options.PartitionCount == 4, "PartitionCount must be exactly 4")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.AddNpgsqlDbContext<PaymentsDbContext>("paymentsdb");
builder.Services.AddScoped<OutboxRepository>();
builder.Services.AddScoped<IOutboxRepository>(services => services.GetRequiredService<OutboxRepository>());
builder.Services.AddScoped<IPaymentStorageHandler, PaymentStorageHandler>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapDefaultEndpoints();
app.Run();
