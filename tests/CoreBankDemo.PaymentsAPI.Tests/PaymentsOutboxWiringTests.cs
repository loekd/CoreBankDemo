using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

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
/// composed graph, replacing only external infrastructure.
/// </summary>
public class PaymentsOutboxWiringTests
{
    [Fact]
    public void Program_entry_point_wires_the_forwarding_processor_correctly()
    {
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

    private sealed class PaymentsApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _connectionString =
            $"Data Source=payments-wiring-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly SqliteConnection _keeper;

        public PaymentsApiFactory()
        {
            _keeper = new SqliteConnection(_connectionString);
            _keeper.Open();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<PaymentsDbContext>>();
                services.RemoveAll<PaymentsDbContext>();
                services.RemoveAll<IDbContextOptionsConfiguration<PaymentsDbContext>>();
                foreach (var descriptor in services
                             .Where(descriptor =>
                                 descriptor.ServiceType.Namespace == "Microsoft.EntityFrameworkCore.Internal" &&
                                 descriptor.ServiceType.IsGenericType &&
                                 descriptor.ServiceType.GenericTypeArguments.Contains(typeof(PaymentsDbContext)))
                             .ToArray())
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<PaymentsDbContext>(options => options.UseSqlite(_connectionString));
                services.RemoveAll<IDistributedLockService>();
                services.AddSingleton<IDistributedLockService, NonAcquiringLockService>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _keeper.Dispose();
            }
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
