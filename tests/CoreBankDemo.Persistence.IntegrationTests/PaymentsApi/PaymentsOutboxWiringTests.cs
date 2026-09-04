using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.ServiceDefaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

/// <summary>
/// Story 5.4 verification gap: the three Program.cs lines that turn payment
/// forwarding on (<c>IOutboxMessageStore&lt;OutboxMessage&gt;</c>,
/// <c>IOutboxDeliveryStrategy&lt;OutboxMessage&gt;</c>,
/// <c>AddHostedService&lt;PaymentsOutboxProcessor&gt;</c>) were previously
/// exercised by no test — <c>PaymentsOutboxProcessorTests</c> builds its own
/// parallel <c>ServiceCollection</c> rather than replaying Program.cs's
/// actual registrations, and Program.cs is excluded from the coverage gate.
/// A dropped or mis-wired line there would leave the app building and
/// starting normally, with the outbox processor silently never forwarding
/// any payment. This test boots the real entry point and inspects its
/// composed graph.
/// </summary>
/// <remarks>
/// ADR-016: the entry point runs against this test's own PostgreSQL database
/// inside the shared container, so <c>AddNpgsqlDbContext</c> and Program.cs's
/// <c>EnsureCreatedAsync</c> execute exactly as deployed — no provider is
/// substituted and no second database engine exists to substitute.
/// </remarks>
[Collection(nameof(PaymentsEntryPointCollection))]
public class PaymentsOutboxWiringTests(PostgresContainerFixture fixture)
    : PaymentsPostgresTestBase(fixture)
{
    [Fact]
    public async Task Program_entry_point_wires_the_forwarding_processor_correctly()
    {
        await using var environment = PaymentsEntryPointEnvironment.Apply(ConnectionString);
        using var factory = new PaymentsApiFactory();
        using var client = factory.CreateClient();

        factory.Services.GetServices<IHostedService>()
            .Should().Contain(service => service.GetType() == typeof(PaymentsOutboxProcessor));

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxMessageStore<OutboxMessage>>();
        store.Should().BeSameAs(scope.ServiceProvider.GetRequiredService<OutboxRepository>());
        scope.ServiceProvider.GetRequiredService<IOutboxDeliveryStrategy<OutboxMessage>>()
            .Should().BeOfType<HttpForwardOutboxDeliveryStrategy>();
    }

    private sealed class PaymentsApiFactory : WebApplicationFactory<PaymentsDbContext>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDistributedLockService>();
                services.AddSingleton<IDistributedLockService, NonAcquiringLockService>();
            });
        }
    }

    private sealed class NonAcquiringLockService : IDistributedLockService
    {
        public Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
