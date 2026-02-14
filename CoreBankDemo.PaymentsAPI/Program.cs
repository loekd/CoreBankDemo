using Microsoft.EntityFrameworkCore;
using CoreBankDemo.PaymentsAPI;

var builder = WebApplication.CreateBuilder(args);

// Add Aspire Service Defaults (includes OpenTelemetry, health checks, service discovery)
builder.AddServiceDefaults();

builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Database for Outbox pattern
builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseSqlite("Data Source=payments.db"));

// Resilience (still using standard resilience handler for retry/circuit breaker)
builder.Services.AddHttpClient("CoreBank")
    .AddStandardResilienceHandler();

// Outbox processor (controlled by feature flag)
var useOutbox = builder.Configuration.GetValue<bool>("Features:UseOutbox");
if (useOutbox)
{
    builder.Services.AddHostedService<OutboxProcessor>();
}

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

// Payment API endpoint that calls the legacy core bank system
app.MapPost("/api/payments", async (PaymentRequest request, IHttpClientFactory httpClientFactory, PaymentsDbContext dbContext, IConfiguration configuration) =>
    {
        var paymentId = Guid.NewGuid().ToString();
        var useOutbox = configuration.GetValue<bool>("Features:UseOutbox");

        if (useOutbox)
        {
            // Store in outbox for later processing
            var outboxMessage = new OutboxMessage
            {
                Id = Guid.NewGuid(),
                PaymentId = paymentId,
                FromAccount = request.FromAccount,
                ToAccount = request.ToAccount,
                Amount = request.Amount,
                Currency = request.Currency,
                CreatedAt = DateTimeOffset.UtcNow,
                Status = "Pending",
                PartitionKey = request.FromAccount // Partition by account for ordering
            };

            dbContext.OutboxMessages.Add(outboxMessage);
            await dbContext.SaveChangesAsync();

            return Results.Accepted($"/api/payments/{paymentId}", new PaymentResponse(
                paymentId,
                "pending",
                "Pending",
                request.Amount,
                request.Currency,
                DateTimeOffset.UtcNow
            ));
        }

        // Direct processing (original behavior)
        var client = httpClientFactory.CreateClient("CoreBank");
        var coreBankUrl = configuration["CoreBankApi:BaseUrl"] ?? "http://localhost:5032";

        try
        {
            // Step 1: Validate account with legacy core bank
            var validationResponse = await client.PostAsJsonAsync(
                $"{coreBankUrl}/api/accounts/validate",
                new { AccountNumber = request.ToAccount });

            validationResponse.EnsureSuccessStatusCode();
            var validation = await validationResponse.Content.ReadFromJsonAsync<AccountValidationResponse>();

            if (validation?.IsValid != true)
            {
                return Results.BadRequest(new { Error = "Invalid account number" });
            }

            // Step 2: Process transaction with legacy core bank
            var transactionResponse = await client.PostAsJsonAsync(
                $"{coreBankUrl}/api/transactions/process",
                new
                {
                    FromAccount = request.FromAccount,
                    ToAccount = request.ToAccount,
                    Amount = request.Amount,
                    Currency = request.Currency,
                    IdempotencyKey = paymentId
                });

            transactionResponse.EnsureSuccessStatusCode();
            var transaction = await transactionResponse.Content.ReadFromJsonAsync<TransactionResponse>();

            return Results.Ok(new PaymentResponse(
                paymentId,
                transaction?.TransactionId ?? "unknown",
                "Completed",
                request.Amount,
                request.Currency,
                DateTimeOffset.UtcNow
            ));
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem(
                title: "Core Bank System Unavailable",
                detail: ex.Message,
                statusCode: 503);
        }
    })
    .WithName("ProcessPayment");

// Query outbox messages for demo purposes
app.MapGet("/api/outbox", async (PaymentsDbContext dbContext) =>
    {
        var messages = await dbContext.OutboxMessages
            .OrderByDescending(m => m.CreatedAt)
            .Take(50)
            .ToListAsync();
        return Results.Ok(messages);
    })
    .WithName("GetOutboxMessages");

app.Run();

public record PaymentRequest(string FromAccount, string ToAccount, decimal Amount, string Currency);

public record PaymentResponse(
    string PaymentId,
    string TransactionId,
    string Status,
    decimal Amount,
    string Currency,
    DateTimeOffset ProcessedAt
);

public record AccountValidationResponse(
    string AccountNumber,
    bool IsValid,
    string? AccountHolderName = null,
    decimal? Balance = null
);

public record TransactionResponse(
    string TransactionId,
    string Status,
    DateTimeOffset ProcessedAt
);