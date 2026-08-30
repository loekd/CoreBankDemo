using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreBankDemo.PaymentsAPI;

public static class PaymentStorageServiceCollectionExtensions
{
    // ADR-010 fixes the system topology at four partitions.
    private const int RequiredPartitionCount = 4;

    public static IServiceCollection AddPaymentStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(OutboxProcessingOptions.SectionName);
        services.AddOptions<OutboxProcessingOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .Validate(
                options => section.Exists() &&
                           int.TryParse(
                               section[nameof(OutboxProcessingOptions.PartitionCount)],
                               out var configuredPartitionCount) &&
                           configuredPartitionCount == RequiredPartitionCount &&
                           options.PartitionCount == RequiredPartitionCount,
                $"OutboxProcessing:PartitionCount must be exactly {RequiredPartitionCount}.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<OutboxRepository>();
        services.AddScoped<IOutboxRepository>(provider => provider.GetRequiredService<OutboxRepository>());
        services.AddScoped<IPaymentStorageHandler, PaymentStorageHandler>();
        return services;
    }
}
