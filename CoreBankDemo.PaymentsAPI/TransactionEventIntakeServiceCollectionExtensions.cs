using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreBankDemo.PaymentsAPI;

/// <summary>
/// Registers Story 5.5's event-subscription intake -- validated
/// <see cref="InboxProcessingOptions"/> (mirroring
/// <see cref="PaymentStorageServiceCollectionExtensions.AddPaymentStorage"/>'s
/// exact-partition-count guard for the outbox), the repository/intake-handler
/// pair, and Dapr's MVC integration so <c>TransactionEventsController</c>'s
/// routes are discoverable -- plus Story 5.6's addition: the same
/// <see cref="InboxMessageRepository"/> instance is also exposed through the
/// kernel's <see cref="IInboxMessageStore{TMessage}"/> port (spec-5-6's code
/// map: "expose the existing instance through the kernel store port"),
/// mirroring <see cref="CoreBankDemo.CoreBankAPI.Inbox.InboxMessageRepository"/>'s
/// dual registration exactly. The processing handler and hosted processor
/// itself are registered in <c>Program.cs</c>, alongside the mirrored outbox
/// hosted-service registrations there.
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
        services.AddScoped<InboxMessageRepository>();
        services.AddScoped<IInboxMessageRepository>(sp => sp.GetRequiredService<InboxMessageRepository>());
        services.AddScoped<IInboxMessageStore<InboxMessage>>(sp => sp.GetRequiredService<InboxMessageRepository>());
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
