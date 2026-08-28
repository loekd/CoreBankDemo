using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreBankDemo.PaymentsAPI;

public static class PaymentStorageServiceCollectionExtensions
{
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
                           configuredPartitionCount == 4 &&
                           options.PartitionCount == 4,
                "OutboxProcessing:PartitionCount must be exactly 4.")
            .ValidateOnStart();

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<OutboxRepository>();
        services.AddScoped<IOutboxRepository>(provider => provider.GetRequiredService<OutboxRepository>());
        services.AddScoped<IPaymentStorageHandler, PaymentStorageHandler>();
        return services;
    }
}
