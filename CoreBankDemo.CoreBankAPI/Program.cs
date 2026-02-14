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

        builder.Services.AddOpenApi();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        
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
                        CreatedAt = DateTimeOffset.UtcNow
                    },
                    new Account
                    {
                        AccountNumber = "NL20INGB0001234567",
                        AccountHolderName = "Jane Smith",
                        Balance = 10000.00m,
                        Currency = "EUR",
                        IsActive = true,
                        CreatedAt = DateTimeOffset.UtcNow
                    },
                    new Account
                    {
                        AccountNumber = "NL39RABO0300065264",
                        AccountHolderName = "Bob Johnson",
                        Balance = 2500.00m,
                        Currency = "EUR",
                        IsActive = true,
                        CreatedAt = DateTimeOffset.UtcNow
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

        // Legacy Core Banking System APIs
        app.MapPost("/api/accounts/validate", async (AccountValidationRequest request, CoreBankDbContext dbContext) =>
            {
                var account = await dbContext.Accounts
                    .FirstOrDefaultAsync(a => a.AccountNumber == request.AccountNumber);

                var isValid = account != null && account.IsActive;

                return Results.Ok(new AccountValidationResponse(
                    request.AccountNumber,
                    isValid,
                    account?.AccountHolderName,
                    account?.Balance
                ));
            })
            .WithName("ValidateAccount");

        app.MapPost("/api/transactions/process", async (TransactionRequest request, CoreBankDbContext dbContext, IConfiguration configuration) =>
            {
                var useInbox = configuration.GetValue<bool>("Features:UseInbox");
                var idempotencyKey = request.IdempotencyKey;

                if (useInbox && !string.IsNullOrEmpty(idempotencyKey))
                {
                    // Check if already received (idempotency check)
                    var existing = await dbContext.InboxMessages
                        .FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey);

                    if (existing != null)
                    {
                        // Already received - return cached response based on status
                        if (existing.Status == "Completed" && !string.IsNullOrEmpty(existing.ResponsePayload))
                        {
                            var cachedResponse = JsonSerializer.Deserialize<TransactionResponse>(existing.ResponsePayload);
                            return Results.Ok(cachedResponse);
                        }
                        else if (existing.Status == "Pending" || existing.Status == "Processing")
                        {
                            // Still processing
                            return Results.Accepted($"/api/transactions/{existing.IdempotencyKey}", new
                            {
                                IdempotencyKey = existing.IdempotencyKey,
                                Status = existing.Status,
                                Message = "Transaction is being processed"
                            });
                        }
                    }

                    // New request - store in inbox
                    var inboxMessage = new InboxMessage
                    {
                        Id = Guid.NewGuid(),
                        IdempotencyKey = idempotencyKey,
                        FromAccount = request.FromAccount,
                        ToAccount = request.ToAccount,
                        Amount = request.Amount,
                        Currency = request.Currency,
                        ReceivedAt = DateTimeOffset.UtcNow,
                        Status = "Pending"
                    };

                    dbContext.InboxMessages.Add(inboxMessage);
                    await dbContext.SaveChangesAsync();

                    return Results.Accepted($"/api/transactions/{idempotencyKey}", new
                    {
                        IdempotencyKey = idempotencyKey,
                        Status = "Pending",
                        Message = "Transaction accepted for processing"
                    });
                }

                // Direct processing (when inbox is disabled)
                var response = new TransactionResponse(
                    Guid.NewGuid().ToString(),
                    "Completed",
                    DateTimeOffset.UtcNow
                );

                return Results.Ok(response);
            })
            .WithName("ProcessTransaction");

        // Query inbox messages for demo purposes
        app.MapGet("/api/inbox", async (CoreBankDbContext dbContext) =>
            {
                var messages = await dbContext.InboxMessages
                    .OrderByDescending(m => m.ReceivedAt)
                    .Take(50)
                    .ToListAsync();
                return Results.Ok(messages);
            })
            .WithName("GetInboxMessages");

        // Query transaction status by idempotency key
        app.MapGet("/api/transactions/{idempotencyKey}", async (string idempotencyKey, CoreBankDbContext dbContext) =>
            {
                var message = await dbContext.InboxMessages
                    .FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey);

                if (message == null)
                    return Results.NotFound(new { Error = "Transaction not found" });

                if (message.Status == "Completed" && !string.IsNullOrEmpty(message.ResponsePayload))
                {
                    var response = JsonSerializer.Deserialize<TransactionResponse>(message.ResponsePayload);
                    return Results.Ok(response);
                }

                return Results.Ok(new
                {
                    IdempotencyKey = message.IdempotencyKey,
                    Status = message.Status,
                    ReceivedAt = message.ReceivedAt,
                    ProcessedAt = message.ProcessedAt
                });
            })
            .WithName("GetTransactionStatus");

        app.Run();
    }
}

public record AccountValidationRequest(string AccountNumber);

public record AccountValidationResponse(
    string AccountNumber,
    bool IsValid,
    string? AccountHolderName = null,
    decimal? Balance = null
);

public record TransactionRequest(
    string FromAccount,
    string ToAccount,
    decimal Amount,
    string Currency,
    string? IdempotencyKey = null
);

public record TransactionResponse(
    string TransactionId,
    string Status,
    DateTimeOffset ProcessedAt
);