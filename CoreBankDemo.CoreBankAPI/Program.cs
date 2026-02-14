using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CoreBankDemo.CoreBankAPI;

public class Program
{
    public static void Main(string[] args)
    {
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

        // Database for Inbox pattern
        builder.Services.AddDbContext<CoreBankDbContext>(options =>
            options.UseSqlite("Data Source=corebank.db"));

        // Inbox processor (controlled by feature flag)
        var useInbox = builder.Configuration.GetValue<bool>("Features:UseInbox");
        if (useInbox)
        {
            builder.Services.AddHostedService<InboxProcessor>();
        }

        var app = builder.Build();
        
        // Ensure database is created and seeded
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();
            db.Database.EnsureCreated();
            
            // Seed accounts if empty
            if (!db.Accounts.Any())
            {
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
}