using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Inbox;
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

// Story 5.5: event subscription intake -- durably stores known
// transaction-events CloudEvent deliveries.
builder.Services.AddTransactionEventIntake(builder.Configuration);

// Story 5.6: event handling processor. IInboxMessageStore<InboxMessage> is
// already exposed by AddTransactionEventIntake above (the same
// InboxMessageRepository instance); TransactionEventHandler is observational
// only (no payment/account state mutation) and enriches the consumer span
// InboxProcessor's InboxProcessorBase<InboxMessage> restores from each
// message's persisted TraceParent/TraceState onto the same
// "CoreBank.PaymentsAPI" ActivitySource already registered by
// AddServiceDefaults above -- never a second ActivitySource.
builder.Services.AddScoped<IInboxMessageHandler<InboxMessage>, TransactionEventHandler>();
builder.Services.AddHostedService<InboxProcessor>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapDefaultEndpoints();

// Story 5.5: unwrap Dapr's structured CloudEvents into the raw event payload
// before routing, so TransactionEventsController's [FromBody] model binding
// deserializes the typed contract directly (Dapr.AspNetCore).
app.UseCloudEvents(new Dapr.CloudEventsMiddlewareOptions
{
    ForwardCloudEventPropertiesAsHeaders = true,
    IncludedCloudEventPropertiesAsHeaders = ["type", "id", "source"]
});

app.MapPaymentIntake();

app.Run();

// Exposed so Microsoft.AspNetCore.Mvc.Testing's WebApplicationFactory<Program>
// can boot the real entry point in tests (spec-5-5's real-entry-point
// CloudEvent POST requirement).
public partial class Program;
