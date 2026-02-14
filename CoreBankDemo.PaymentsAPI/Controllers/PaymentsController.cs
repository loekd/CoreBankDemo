using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CoreBankDemo.PaymentsAPI.Models;
using Dapr.Client;

namespace CoreBankDemo.PaymentsAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly DaprClient _daprClient;
    private readonly PaymentsDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;

    public PaymentsController(
        DaprClient daprClient,
        PaymentsDbContext dbContext,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        _daprClient = daprClient;
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

        // Direct processing using Dapr service invocation
        try
        {
            // Step 1: Validate account with legacy core bank using Dapr
            var validation = await _daprClient.InvokeMethodAsync<object, AccountValidationResponse>(
                "corebank-api",
                "api/accounts/validate",
                new { AccountNumber = request.ToAccount });

            if (validation?.IsValid != true)
            {
                return BadRequest(new { Errors = new[] { "Invalid account number" } });
            }

            // Step 2: Process transaction with legacy core bank using Dapr
            var transaction = await _daprClient.InvokeMethodAsync<object, TransactionResponse>(
                "corebank-api",
                "api/transactions/process",
                new
                {
                    FromAccount = request.FromAccount,
                    ToAccount = request.ToAccount,
                    Amount = request.Amount,
                    Currency = request.Currency,
                    IdempotencyKey = paymentId
                });

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
        catch (Exception ex)
        {
            return Problem(
                title: "Core Bank System Unavailable",
                detail: ex.Message,
                statusCode: 503);
        }
    }
}
