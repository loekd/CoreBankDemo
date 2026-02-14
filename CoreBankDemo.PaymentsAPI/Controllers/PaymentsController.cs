using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CoreBankDemo.PaymentsAPI.Models;

namespace CoreBankDemo.PaymentsAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PaymentsDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;

    public PaymentsController(
        IHttpClientFactory httpClientFactory,
        PaymentsDbContext dbContext,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        _httpClientFactory = httpClientFactory;
        _dbContext = dbContext;
        _configuration = configuration;
        _timeProvider = timeProvider;
    }

    [HttpPost]
    public async Task<IActionResult> ProcessPayment([FromBody] PaymentRequest request)
    {
        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .ToList();
            return BadRequest(new { Errors = errors });
        }

        var paymentId = Guid.NewGuid().ToString();
        var useOutbox = _configuration.GetValue<bool>("Features:UseOutbox");

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
                CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                Status = "Pending",
                PartitionKey = request.FromAccount // Partition by account for ordering
            };

            _dbContext.OutboxMessages.Add(outboxMessage);
            await _dbContext.SaveChangesAsync();

            var response = new PaymentResponse(
                paymentId,
                "pending",
                "Pending",
                request.Amount,
                request.Currency,
                _timeProvider.GetUtcNow()
            );

            return Accepted($"/api/payments/{paymentId}", response);
        }

        // Direct processing (original behavior)
        var client = _httpClientFactory.CreateClient("CoreBank");
        var coreBankUrl = _configuration["CoreBankApi:BaseUrl"] ?? "http://localhost:5032";

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
                return BadRequest(new { Errors = new[] { "Invalid account number" } });
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

            var successResponse = new PaymentResponse(
                paymentId,
                transaction?.TransactionId ?? "unknown",
                "Completed",
                request.Amount,
                request.Currency,
                _timeProvider.GetUtcNow()
            );

            return Ok(successResponse);
        }
        catch (HttpRequestException ex)
        {
            return Problem(
                title: "Core Bank System Unavailable",
                detail: ex.Message,
                statusCode: 503);
        }
    }
}
