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
            return BadRequest(new { Errors = GetModelErrors() });

        var paymentId = Guid.NewGuid().ToString();
        var messageId = Guid.NewGuid().ToString();
        var useOutbox = _configuration.GetValue<bool>("Features:UseOutbox");

        if (useOutbox)
            return await ProcessWithOutbox(request, paymentId, messageId);

        return await ProcessDirectly(request, paymentId);
    }

    private async Task<IActionResult> ProcessWithOutbox(PaymentRequest request, string paymentId, string messageId)
    {
        // Check for duplicate message
        var existingMessage = await _dbContext.OutboxMessages
            .FirstOrDefaultAsync(m => m.MessageId == messageId);

        if (existingMessage != null)
        {
            return Accepted($"/api/payments/{existingMessage.PaymentId}",
                CreatePendingResponse(existingMessage.PaymentId, request));
        }

        var outboxMessage = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            PaymentId = paymentId,
            FromAccount = request.FromAccount,
            ToAccount = request.ToAccount,
            Amount = request.Amount,
            Currency = request.Currency,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            Status = "Pending"
        };

        _dbContext.OutboxMessages.Add(outboxMessage);
        await _dbContext.SaveChangesAsync();

        return Accepted($"/api/payments/{paymentId}", CreatePendingResponse(paymentId, request));
    }

    private async Task<IActionResult> ProcessDirectly(PaymentRequest request, string paymentId)
    {
        try
        {
            if (!await ValidateAccount(request.ToAccount))
                return BadRequest(new { Errors = new[] { "Invalid account number" } });

            var transaction = await ProcessTransaction(request, paymentId);

            return Ok(CreateSuccessResponse(paymentId, transaction, request));
        }
        catch (Exception ex)
        {
            return Problem(title: "Core Bank System Unavailable", detail: ex.Message, statusCode: 503);
        }
    }

    private async Task<bool> ValidateAccount(string accountNumber)
    {
        var validation = await _daprClient.InvokeMethodAsync<object, AccountValidationResponse>(
            "corebank-api",
            "api/accounts/validate",
            new { AccountNumber = accountNumber });

        return validation?.IsValid == true;
    }

    private async Task<TransactionResponse> ProcessTransaction(PaymentRequest request, string paymentId)
    {
        return await _daprClient.InvokeMethodAsync<object, TransactionResponse>(
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
    }

    private List<string> GetModelErrors()
    {
        return ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .ToList();
    }

    private PaymentResponse CreatePendingResponse(string paymentId, PaymentRequest request)
    {
        return new PaymentResponse(
            paymentId,
            "pending",
            "Pending",
            request.Amount,
            request.Currency,
            _timeProvider.GetUtcNow()
        );
    }

    private PaymentResponse CreateSuccessResponse(string paymentId, TransactionResponse transaction, PaymentRequest request)
    {
        return new PaymentResponse(
            paymentId,
            transaction?.TransactionId ?? "unknown",
            "Completed",
            request.Amount,
            request.Currency,
            _timeProvider.GetUtcNow()
        );
    }
}
