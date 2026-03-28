using System.Diagnostics;
using CoreBankDemo.Messaging.Outbox;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;
using static CoreBankDemo.Messaging.MessageConstants;

namespace CoreBankDemo.PaymentsAPI.Outbox;

public class OutboxProcessor : OutboxProcessorBase<OutboxMessage, PaymentsDbContext>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxProcessor> _logger;

    public OutboxProcessor(
        IServiceProvider serviceProvider,
        ILogger<OutboxProcessor> logger,
        IDistributedLockService lockService,
        TimeProvider timeProvider,
        IOptions<OutboxProcessingOptions> options)
        : base(serviceProvider, logger, lockService, timeProvider, options, nameof(OutboxProcessor))
    {
        _serviceProvider = serviceProvider;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override string LockNamePrefix => "payments-outbox";

    protected override async Task ProcessMessageAsync(
        OutboxMessage message,
        IServiceProvider scopedServiceProvider,
        CancellationToken cancellationToken)
    {
        var coreBankApiClient = GetService<ICoreBankApiClient>(scopedServiceProvider);
        var dbContext = GetService<PaymentsDbContext>(scopedServiceProvider);

        var validationResult = await ValidateAccountAsync(coreBankApiClient, message, cancellationToken);

        if (!validationResult.IsValid)
        {
            message.Status = Status.Failed;
            message.LastError = validationResult.Error;
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogWarning("Outbox message {MessageId} failed validation: {Error}",
                message.Id, validationResult.Error);
            return;
        }

        await coreBankApiClient.ProcessTransactionAsync(message, cancellationToken);

        message.Status = Status.Completed;
        message.ProcessedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Successfully processed outbox message {MessageId} for payment {PaymentId}",
            message.Id, message.TransactionId);
    }

    private static async Task<ValidationResult> ValidateAccountAsync(
        ICoreBankApiClient coreBankApiClient,
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        var response = await coreBankApiClient.ValidateAccountAsync(message.ToAccount, cancellationToken);

        return response.IsValid
            ? ValidationResult.Success()
            : ValidationResult.Failure("Invalid account number");
    }

    private record ValidationResult(bool IsValid, string? Error)
    {
        public static ValidationResult Success() => new(true, null);
        public static ValidationResult Failure(string error) => new(false, error);
    }
}
