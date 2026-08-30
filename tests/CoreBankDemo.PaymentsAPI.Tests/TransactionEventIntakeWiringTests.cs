using System.Net;
using System.Text;
using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

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
/// <c>Program</c> via <see cref="WebApplicationFactory{TEntryPoint}"/> --
/// only the database provider (Npgsql -&gt; an in-memory Sqlite double) and
/// the Redis connection string (a local, non-connecting placeholder) are
/// substituted, so the app never needs live infrastructure -- and posts a
/// structured CloudEvent exactly as Dapr's declarative subscription would,
/// proving production middleware/DI/routing together with a durable row and
/// duplicate-delivery HTTP 200 acknowledgement.
/// </summary>
[Collection(nameof(TransactionEventIntakeWiringTests))]
public class TransactionEventIntakeWiringTests : IDisposable
{
    private readonly string _connectionString =
        $"Data Source=wiring-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keeper;
    private readonly string? _previousPaymentsConnectionString;
    private readonly string? _previousRedisConnectionString;

    public TransactionEventIntakeWiringTests()
    {
        _previousPaymentsConnectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__paymentsdb");
        _previousRedisConnectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__redis");

        // WebApplication.CreateBuilder(args) reads environment variables
        // synchronously as Program.cs's very first configuration source, so
        // these are already visible by the time Program.cs's
        // builder.AddNpgsqlDbContext(...)/builder.AddRedisClient(...) calls
        // run -- unlike WithWebHostBuilder's ConfigureServices/
        // ConfigureAppConfiguration callbacks below, which only apply once
        // WebApplicationBuilder.Build() actually executes.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__paymentsdb", "Host=unused;Database=unused;Username=unused;Password=unused");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__redis", "localhost:6379,abortConnect=false,connectTimeout=100");

        _keeper = new SqliteConnection(_connectionString);
        _keeper.Open();
        using var context = CreateVerificationContext();
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _keeper.Dispose();
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__paymentsdb", _previousPaymentsConnectionString);
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__redis", _previousRedisConnectionString);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Real_entry_point_stores_one_row_and_acknowledges_a_redelivery_with_200()
    {
        await using var factory = new PaymentsApiFactory(_connectionString);
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

        await using var verification = CreateVerificationContext();
        verification.InboxMessages.Count(m => m.TransactionId == "txn-wiring-1").Should().Be(1);
    }

    [Fact]
    public void Real_entry_point_registers_the_transaction_event_processor_and_scoped_handler()
    {
        using var factory = new PaymentsApiFactory(_connectionString);
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
        await using var factory = new PaymentsApiFactory(_connectionString, processInbox: true);
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
            await using var context = CreateVerificationContext();
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
        await using var factory = new PaymentsApiFactory(_connectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/events/transactions/unknown",
            CloudEvent("evt-wiring-3", "com.corebank.unknown.type", "{}"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verification = CreateVerificationContext();
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
        await using var factory = new PaymentsApiFactory(_connectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            route,
            CloudEvent($"evt-{transactionId}", eventType, data),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verification = CreateVerificationContext();
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
            var manifest = File.ReadAllText(Path.Combine(root, relativePath));
            manifest.Should().Contain($"event.type == \"{Constants.TransactionCompleted}\"");
            manifest.Should().Contain("path: /events/transactions/completed");
            manifest.Should().Contain($"event.type == \"{Constants.TransactionFailed}\"");
            manifest.Should().Contain("path: /events/transactions/failed");
            manifest.Should().Contain($"event.type == \"{Constants.BalanceUpdated}\"");
            manifest.Should().Contain("path: /events/transactions/balance-updated");
            manifest.Should().Contain("default: /events/transactions/unknown");
        }
    }

    private PaymentsDbContext CreateVerificationContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlite(_connectionString).Options);

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
    /// Boots the real public <c>Program</c> entry point, swapping only the
    /// database provider so no live Postgres is required and removing only
    /// the outbox forwarding hosted service (story 5.4) -- not this
    /// fixture's concern, and it would otherwise contact Redis as soon as
    /// the host starts. The event-handling processor (<see cref="InboxProcessor"/>,
    /// story 5.6) is deliberately kept registered here, unlike before: it
    /// used to be removed specifically because its singleton hosted service
    /// once ctor-injected the scoped <see cref="IInboxMessageStore{TMessage}"/>
    /// directly, a captive-dependency defect that
    /// <see cref="ServiceProviderOptions.ValidateScopes"/>/
    /// <see cref="ServiceProviderOptions.ValidateOnBuild"/> (explicitly
    /// enabled below, mirroring the real container validation a production
    /// composition root is subject to) would reject at
    /// <c>host.Build()</c> time. Now that the kernel resolves the store from
    /// its own per-partition DI scope instead (never a ctor-injected field),
    /// this factory proves the real production registration graph — with
    /// <see cref="InboxProcessor"/> included — builds cleanly under that same
    /// strict validation, closing the verification gap the removal used to
    /// paper over. <see cref="TransactionEventProcessorWiringTests"/> proves
    /// the same registration sequence separately, without a real HTTP host.
    /// </summary>
    private sealed class PaymentsApiFactory(
        string connectionString,
        bool processInbox = false) : WebApplicationFactory<Program>
    {
        // Aspire's AddNpgsqlDbContext also registers Npgsql's internal EF Core
        // provider services directly on IServiceCollection (not scoped to
        // DbContextOptions<T>), so simply removing PaymentsDbContext's
        // descriptors still leaves two competing database providers visible
        // to the ambient container. An isolated internal service provider
        // containing only the Sqlite provider sidesteps that conflict
        // entirely -- the standard pattern for substituting providers in
        // WebApplicationFactory-based tests.
        private static readonly IServiceProvider SqliteProviderServices =
            new ServiceCollection().AddEntityFrameworkSqlite().BuildServiceProvider();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Explicitly enable scope validation regardless of the ambient
            // hosting environment, so this factory always proves production
            // composition -- including the hosted, singleton InboxProcessor
            // below -- builds cleanly against a container that rejects
            // captive dependencies, the same way a real deployed composition
            // root would in Development.
            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            });

            builder.ConfigureServices(services =>
            {
                // Aspire's AddNpgsqlDbContext pools PaymentsDbContext. Remove
                // every internal EF service closed over this context without
                // compiling against unsupported EF implementation interfaces.
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

                var outboxProcessor = services.SingleOrDefault(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService) &&
                    descriptor.ImplementationType == typeof(PaymentsOutboxProcessor));
                if (outboxProcessor is not null)
                {
                    services.Remove(outboxProcessor);
                }

                // InboxProcessor (story 5.6) is deliberately left registered
                // -- see this factory's class doc -- so ValidateOnBuild above
                // proves the real production composition, singleton hosted
                // service included, builds without a captive scoped store.

                services.AddDbContext<PaymentsDbContext>(options =>
                    options.UseSqlite(connectionString).UseInternalServiceProvider(SqliteProviderServices));
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

[CollectionDefinition(nameof(TransactionEventIntakeWiringTests), DisableParallelization = true)]
public sealed class TransactionEventIntakeWiringTestCollection;
