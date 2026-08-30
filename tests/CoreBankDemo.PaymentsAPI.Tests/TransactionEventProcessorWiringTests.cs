using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// Story 5.6's counterpart to <see cref="PaymentsOutboxWiringTests"/>: proves
/// the actual <c>Program.cs</c> registration sequence for the event handling
/// processor -- <c>AddTransactionEventIntake</c>'s
/// <see cref="IInboxMessageStore{TMessage}"/> exposure plus the two lines
/// <c>Program.cs</c> adds for this story
/// (<c>AddScoped&lt;IInboxMessageHandler&lt;InboxMessage&gt;,
/// TransactionEventHandler&gt;</c> and
/// <c>AddHostedService&lt;InboxProcessor&gt;</c>) -- composes correctly. A
/// dropped or mis-wired line here would leave the app building and starting
/// normally, with the processor silently never dispatching any stored event
/// (the same verification gap <see cref="PaymentsOutboxWiringTests"/> closes
/// for story 5.4's forwarding processor).
/// </summary>
public class TransactionEventProcessorWiringTests
{
    [Fact]
    public void Program_cs_registration_sequence_wires_the_event_handling_processor_correctly()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration["InboxProcessing:PartitionCount"] = "4";
        builder.Configuration["InboxProcessing:LockExpirySeconds"] = "30";
        builder.Configuration["InboxProcessing:PollingIntervalMs"] = "200";

        // Mirrors Program.cs exactly (including the service-defaults
        // registration that supplies the reused "CoreBank.PaymentsAPI"
        // ActivitySource and IDistributedLockService). This test only inspects
        // the composed graph, so the real Npgsql provider is registered against
        // a connection string that is never opened -- the durable behavior it
        // would exercise is proved in
        // CoreBankDemo.Persistence.IntegrationTests (ADR-016).
        builder.AddServiceDefaults("CoreBank.PaymentsAPI");
        builder.Services.AddDbContext<PaymentsDbContext>(
            options => options.UseNpgsql(TestConnectionStrings.NeverConnected));
        builder.Services.AddTransactionEventIntake(builder.Configuration);
        builder.Services.AddScoped<IInboxMessageHandler<InboxMessage>, TransactionEventHandler>();
        builder.Services.AddHostedService<InboxProcessor>();

        // ValidateOnBuild mirrors the strict validation a real ASP.NET Core
        // composition root applies (WebApplicationFactory-based tests get
        // this by default -- see TransactionEventIntakeWiringTests): it
        // would have caught the fixed captive-dependency defect where this
        // singleton hosted InboxProcessor once ctor-injected the scoped
        // IInboxMessageStore<InboxMessage> directly.
        using var provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        provider.GetServices<IHostedService>()
            .Should().Contain(service => service.GetType() == typeof(InboxProcessor));

        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IInboxMessageStore<InboxMessage>>();
        store.Should().BeSameAs(scope.ServiceProvider.GetRequiredService<InboxMessageRepository>());
        scope.ServiceProvider.GetRequiredService<IInboxMessageHandler<InboxMessage>>()
            .Should().BeOfType<TransactionEventHandler>();
    }
}
