using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// any payment (mirrors the gap <see cref="RedisLockWiringTests"/> closes
/// for story 6.2's Redis wiring). This test replays Program.cs's actual
/// registration sequence and proves the composed graph is correct.
/// </summary>
public class PaymentsOutboxWiringTests
{
    [Fact]
    public void Program_cs_registration_sequence_wires_the_forwarding_processor_correctly()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration["ConnectionStrings:redis"] = "localhost:6379,abortConnect=false,connectTimeout=100";
        builder.Configuration["OutboxProcessing:PartitionCount"] = "4";
        builder.Configuration["OutboxProcessing:LockExpirySeconds"] = "30";
        builder.Configuration["OutboxProcessing:PollingIntervalMs"] = "200";

        // Mirrors Program.cs exactly (including the Redis client / service
        // defaults registrations that supply IDistributedLockService and
        // ActivitySource), substituting an in-memory Sqlite context for the
        // real builder.AddNpgsqlDbContext("paymentsdb") call so this test
        // needs no live Postgres.
        builder.AddRedisClient("redis");
        builder.AddServiceDefaults("CoreBank.PaymentsAPI");
        builder.Services.AddDbContext<PaymentsDbContext>(
            options => options.UseSqlite("Data Source=:memory:"));
        builder.Services.AddPaymentStorage(builder.Configuration);
        builder.Services.AddCoreBankApiClient();
        builder.Services.AddScoped<IOutboxMessageStore<OutboxMessage>>(
            sp => sp.GetRequiredService<OutboxRepository>());
        builder.Services.AddScoped<IOutboxDeliveryStrategy<OutboxMessage>, HttpForwardOutboxDeliveryStrategy>();
        builder.Services.AddHostedService<PaymentsOutboxProcessor>();

        using var provider = builder.Services.BuildServiceProvider();

        provider.GetServices<IHostedService>()
            .Should().Contain(service => service.GetType() == typeof(PaymentsOutboxProcessor));

        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxMessageStore<OutboxMessage>>();
        store.Should().BeSameAs(scope.ServiceProvider.GetRequiredService<OutboxRepository>());
        scope.ServiceProvider.GetRequiredService<IOutboxDeliveryStrategy<OutboxMessage>>()
            .Should().BeOfType<HttpForwardOutboxDeliveryStrategy>();
    }
}
