using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Outbox;

namespace CoreBankDemo.CoreBankAPI;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add Aspire Service Defaults (includes OpenTelemetry, health checks, service discovery)
        builder.AddServiceDefaults("CoreBank.CoreBankAPI", new[] { nameof(InboxProcessor), nameof(MessagingOutboxProcessor) });

        // Add configuration options with validation
        builder.AddInboxProcessingOptions();
        builder.AddMessagingOutboxProcessingOptions();

        // Add Dapr
        builder.Services.AddControllers().AddDapr();
        builder.Services.AddDaprClient();
        builder.Services.AddOpenApi();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        // Add TimeProvider
        builder.Services.AddSingleton(TimeProvider.System);

        // Database for Inbox pattern
        builder.AddNpgsqlDbContext<CoreBankDbContext>("corebankdb");
        
        //Register all services
        builder.Services.AddHostedService<InboxProcessor>();
        builder.Services.AddHostedService<MessagingOutboxProcessor>();
        
        builder.Services.AddScoped<IInboxMessageRepository, InboxMessageRepository>();
        builder.Services.AddScoped<IAccountRepository, AccountRepository>();
        builder.Services.AddTransient<ITransactionExecutor, TransactionExecutor>();
        builder.Services.AddTransient<IOutboxPublisher, OutboxPublisher>();
        builder.Services.AddTransient<TransactionValidator>();

        var app = builder.Build();
        
        // Ensure database is created and seeded
        InitializeDatabaseWithSeedAccounts(app);

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
    }

    private static void InitializeDatabaseWithSeedAccounts(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();
        db.Database.EnsureCreated();
            
        // Seed accounts if empty
        if (db.Accounts.Any()) 
            return;
        
        var accounts = new[]
        {
            new Account
            {
                AccountNumber = "NL91ABNA0417164300",
                AccountHolderName = "John Doe",
                Balance = 5000.00m,
                Currency = "EUR",
                IsActive = true,
                CreatedAt = TimeProvider.System.GetUtcNow().UtcDateTime
            },
            new Account
            {
                AccountNumber = "NL20INGB0001234567",
                AccountHolderName = "Jane Smith",
                Balance = 10000.00m,
                Currency = "EUR",
                IsActive = true,
                CreatedAt = TimeProvider.System.GetUtcNow().UtcDateTime
            },
            new Account
            {
                AccountNumber = "NL39RABO0300065264",
                AccountHolderName = "Bob Johnson",
                Balance = 2500.00m,
                Currency = "EUR",
                IsActive = true,
                CreatedAt = TimeProvider.System.GetUtcNow().UtcDateTime
            }
        };
                
        db.Accounts.AddRange(accounts);
        db.SaveChanges();
    }
}