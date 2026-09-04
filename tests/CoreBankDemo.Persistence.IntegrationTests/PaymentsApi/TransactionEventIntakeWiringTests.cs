using System.Net;
using System.Text;
using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

/// <summary>
/// Story 5.5 review finding (verification gap): every other test in this
/// story exercises the handler/repository/controller directly or replays a
/// reconstructed <see cref="IServiceCollection"/>, so none of them prove
/// that PaymentsAPI's actual production <c>Program</c> entry point wires the
/// Dapr CloudEvents middleware, MVC routing, and intake DI together
/// correctly. A dropped <c>app.UseCloudEvents()</c> call, a missing
/// <c>AddTransactionEventIntake</c> registration, or a routing mismatch
/// against <c>dapr/components*/subscription-transaction-events.yaml</c>
/// would all leave the reconstructed-graph tests green while the deployed
/// service silently rejected every Dapr delivery. This test boots the real
/// <c>Program</c> via <see cref="WebApplicationFactory{TEntryPoint}"/>
/// against this test's own PostgreSQL database inside the shared container
/// (ADR-016) -- the real Npgsql provider and Program.cs's own
/// <c>EnsureCreatedAsync</c> run unmodified; only the Redis connection string
/// (a local, non-connecting placeholder) is substituted -- and posts a
/// structured CloudEvent exactly as Dapr's declarative subscription would,
/// proving production middleware/DI/routing together with a durable row and
/// duplicate-delivery HTTP 200 acknowledgement.
/// </summary>
[Collection(nameof(PaymentsEntryPointCollection))]
public class TransactionEventIntakeWiringTests(PostgresContainerFixture fixture)
    : PaymentsPostgresTestBase(fixture)
{
    [Fact]
    public async Task Real_entry_point_stores_one_row_and_acknowledges_a_redelivery_with_200()
    {
        await using var environment = PaymentsEntryPointEnvironment.Apply(ConnectionString);
        await using var factory = new PaymentsApiFactory();
        using var client = factory.CreateClient();
        const string data = """{"transactionId":"txn-wiring-1","status":"Completed","processedAt":"2026-08-29T12:00:00+00:00"}""";

        var first = await client.PostAsync(
            "/events/transactions/completed",
            CloudEvent("evt-wiring-1", "com.corebank.transaction.completed", data),
            TestContext.Current.CancellationToken);
        var second = await client.PostAsync(
            "/events/transactions/completed",
            CloudEvent("evt-wiring-2", "com.corebank.transaction.completed", data),
            TestContext.Current.CancellationToken);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verification = CreateContext();
        verification.InboxMessages.Count(m => m.TransactionId == "txn-wiring-1").Should().Be(1);
    }

    [Fact]
    public async Task Real_entry_point_registers_the_transaction_event_processor_and_scoped_handler()
    {
        await using var environment = PaymentsEntryPointEnvironment.Apply(ConnectionString);
        await using var factory = new PaymentsApiFactory();
        using var client = factory.CreateClient();

        factory.Services.GetServices<IHostedService>()
            .Should().Contain(service => service.GetType() == typeof(InboxProcessor));

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IInboxMessageHandler<InboxMessage>>()
            .Should().BeOfType<TransactionEventHandler>();
    }

    [Fact]
    public async Task Real_entry_point_accepts_and_processes_a_completed_event()
    {
        await using var environment = PaymentsEntryPointEnvironment.Apply(ConnectionString);
        await using var factory = new PaymentsApiFactory(processInbox: true);
        using var client = factory.CreateClient();
        const string transactionId = "txn-wiring-processed";
        const string data = """{"transactionId":"txn-wiring-processed","status":"Completed","processedAt":"2026-08-29T12:00:00+00:00"}""";

        var response = await client.PostAsync(
            "/events/transactions/completed",
            CloudEvent("evt-wiring-processed", Constants.TransactionCompleted, data),
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        InboxMessage? persisted;
        do
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            await using var context = CreateContext();
            persisted = await context.InboxMessages
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    message => message.TransactionId == transactionId,
                    TestContext.Current.CancellationToken);
        }
        while (persisted?.Status != MessageConstants.Status.Completed && DateTime.UtcNow < deadline);

        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(MessageConstants.Status.Completed);
        persisted.RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task Real_entry_point_acknowledges_an_unsupported_type_on_the_default_route_without_storing()
    {
        await using var environment = PaymentsEntryPointEnvironment.Apply(ConnectionString);
        await using var factory = new PaymentsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/events/transactions/unknown",
            CloudEvent("evt-wiring-3", "com.corebank.unknown.type", "{}"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verification = CreateContext();
        verification.InboxMessages.Count().Should().Be(0);
    }

    [Theory]
    [InlineData(
        "/events/transactions/failed",
        Constants.TransactionFailed,
        """{"transactionId":"txn-wiring-failed","status":"Failed","processedAt":"2026-08-29T12:00:00+00:00","errorReason":"declined"}""",
        "txn-wiring-failed",
        "")]
    [InlineData(
        "/events/transactions/balance-updated",
        Constants.BalanceUpdated,
        """{"transactionId":"txn-wiring-balance","accountNumber":"NL91ABNA0417164300","delta":-10.00,"newBalance":90.00,"currency":"EUR"}""",
        "txn-wiring-balance",
        "NL91ABNA0417164300")]
    public async Task Real_entry_point_binds_and_stores_each_remaining_supported_event(
        string route,
        string eventType,
        string data,
        string transactionId,
        string accountNumber)
    {
        await using var environment = PaymentsEntryPointEnvironment.Apply(ConnectionString);
        await using var factory = new PaymentsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            route,
            CloudEvent($"evt-{transactionId}", eventType, data),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verification = CreateContext();
        verification.InboxMessages.Should().ContainSingle(message =>
            message.TransactionId == transactionId &&
            message.IdempotencyKey == transactionId &&
            message.EventType == eventType &&
            message.AccountNumber == accountNumber);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("3")]
    [InlineData("5")]
    public void Registration_rejects_missing_or_non_four_partition_configuration(
        string? partitionCount)
    {
        var values = new Dictionary<string, string?>
        {
            ["InboxProcessing:LockExpirySeconds"] = "30",
            ["InboxProcessing:PollingIntervalMs"] = "200"
        };
        if (partitionCount is not null)
        {
            values["InboxProcessing:PartitionCount"] = partitionCount;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddTransactionEventIntake(configuration);
        using var provider = services.BuildServiceProvider();

        var act = provider.GetRequiredService<IStartupValidator>().Validate;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Subscription_manifests_match_the_supported_types_and_routes()
    {
        var root = FindRepoRoot();
        foreach (var relativePath in new[]
                 {
                     "dapr/components/subscription-transaction-events.yaml",
                     "dapr/components-loadtest/subscription-transaction-events.yaml"
                 })
        {
            var manifest = File.ReadAllText(Path.Combine(root, relativePath)).ReplaceLineEndings("\n");
            manifest.Should().Contain(
                $"""
                      - match: event.type == "{Constants.TransactionCompleted}"
                        path: /events/transactions/completed
                """);
            manifest.Should().Contain(
                $"""
                      - match: event.type == "{Constants.TransactionFailed}"
                        path: /events/transactions/failed
                """);
            manifest.Should().Contain(
                $"""
                      - match: event.type == "{Constants.BalanceUpdated}"
                        path: /events/transactions/balance-updated
                """);
            manifest.Should().Contain("default: /events/transactions/unknown");
        }
    }

    private static StringContent CloudEvent(string id, string type, string data) =>
        new(
            $$"""
            {
              "specversion": "1.0",
              "type": "{{type}}",
              "source": "test-harness",
              "id": "{{id}}",
              "datacontenttype": "application/json",
              "data": {{data}}
            }
            """,
            Encoding.UTF8,
            "application/cloudevents+json");

    /// <summary>
    /// Boots the real public <c>Program</c> entry point against this test's own
    /// PostgreSQL database, removing only the outbox forwarding hosted service
    /// (story 5.4) -- not this fixture's concern, and it would otherwise
    /// contact Redis as soon as the host starts. The event-handling processor
    /// (<see cref="InboxProcessor"/>, story 5.6) is deliberately kept
    /// registered: its singleton hosted service once ctor-injected the scoped
    /// <see cref="IInboxMessageStore{TMessage}"/> directly, a captive-dependency
    /// defect that <see cref="ServiceProviderOptions.ValidateScopes"/>/
    /// <see cref="ServiceProviderOptions.ValidateOnBuild"/> (explicitly enabled
    /// below, mirroring the real container validation a production composition
    /// root is subject to) would reject at <c>host.Build()</c> time. Now that
    /// the kernel resolves the store from its own per-partition DI scope
    /// instead, this factory proves the real production registration graph --
    /// with <see cref="InboxProcessor"/> included -- builds cleanly under that
    /// same strict validation. <see cref="TransactionEventProcessorWiringTests"/>
    /// proves the same registration sequence separately, without a real HTTP
    /// host.
    /// </summary>
    private sealed class PaymentsApiFactory(bool processInbox = false)
        : WebApplicationFactory<PaymentsDbContext>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Explicitly enable scope validation regardless of the ambient
            // hosting environment, so this factory always proves production
            // composition -- including the hosted, singleton InboxProcessor --
            // builds cleanly against a container that rejects captive
            // dependencies, the same way a real deployed composition root would
            // in Development.
            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            });

            builder.ConfigureServices(services =>
            {
                var outboxProcessor = services.SingleOrDefault(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService) &&
                    descriptor.ImplementationType == typeof(PaymentsOutboxProcessor));
                if (outboxProcessor is not null)
                {
                    services.Remove(outboxProcessor);
                }

                services.RemoveAll<IDistributedLockService>();
                if (processInbox)
                {
                    services.AddSingleton<IDistributedLockService, AcquiringLockService>();
                }
                else
                {
                    services.AddSingleton<IDistributedLockService, NonAcquiringLockService>();
                }
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

    private sealed class AcquiringLockService : IDistributedLockService
    {
        public async Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            await workload(cancellationToken);
            return true;
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CoreBankDemo.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
