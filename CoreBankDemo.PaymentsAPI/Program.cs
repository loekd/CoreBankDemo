using CoreBankDemo.PaymentsAPI;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("CoreBank.PaymentsAPI");

builder.Services.AddHealthChecks()
    .AddDbContextCheck<PaymentsDbContext>("payments-db");

builder.AddNpgsqlDbContext<PaymentsDbContext>("paymentsdb");
builder.Services.AddPaymentStorage(builder.Configuration);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapDefaultEndpoints();

app.Run();
