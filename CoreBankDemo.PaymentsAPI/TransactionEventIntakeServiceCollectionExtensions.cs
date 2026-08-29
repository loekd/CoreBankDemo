using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreBankDemo.PaymentsAPI;

/// <summary>
/// Registers Story 5.5's event-subscription intake: validated
/// <see cref="InboxProcessingOptions"/> (mirroring
/// <see cref="PaymentStorageServiceCollectionExtensions.AddPaymentStorage"/>'s
/// exact-partition-count guard for the outbox), the repository/handler pair,
/// and Dapr's MVC integration so <c>TransactionEventsController</c>'s routes
/// are discoverable. No processor is registered here -- Story 5.6 owns
/// dispatch.
/// </summary>
internal static class TransactionEventIntakeServiceCollectionExtensions
{
    internal static IServiceCollection AddTransactionEventIntake(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(InboxProcessingOptions.SectionName);
        services.AddOptions<InboxProcessingOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .Validate(
                options => section.Exists() &&
                           int.TryParse(
                               section[nameof(InboxProcessingOptions.PartitionCount)],
                               out var configuredPartitionCount) &&
                           configuredPartitionCount == 4 &&
                           options.PartitionCount == 4,
                "InboxProcessing:PartitionCount must be exactly 4.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IInboxMessageRepository, InboxMessageRepository>();
        services.AddScoped<ITransactionEventIntakeHandler, TransactionEventIntakeHandler>();

        // Dapr's declarative "transaction-events" subscription (both
        // dapr/components*/subscription-transaction-events.yaml manifests)
        // posts structured CloudEvents to TransactionEventsController's
        // routes; .AddDapr() wires the MVC-side Dapr integration those
        // deliveries rely on.
        services.AddControllers().AddDapr();

        return services;
    }
}
