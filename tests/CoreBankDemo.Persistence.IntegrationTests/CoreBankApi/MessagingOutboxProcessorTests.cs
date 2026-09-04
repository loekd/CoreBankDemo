using System.Diagnostics;
using System.Reflection;
using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.CoreBankApi;

public class MessagingOutboxProcessorTests(PostgresContainerFixture fixture) : CoreBankApiPostgresTestBase(fixture)
{
    [Fact]
    public async Task StartAsync_publishes_and_completes_a_claimed_row()
    {
        await SeedMessageAsync();
        var publisher = new Mock<IEventPublisher>();
        using var services = BuildServices(publisher.Object);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockService = new SingleTickLockService(completion);
        var processor = CreateProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            completion,
            lockService);

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        await using var verifyContext = CreateContext();
        var row = await verifyContext.MessagingOutboxMessages
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        row.Status.Should().Be(MessageConstants.Status.Completed);
        row.RetryCount.Should().Be(0);
        lockService.LockName.Should().Be("messaging-outbox-partition-0");
        publisher.Verify(p => p.PublishAsync(
            row.EventType,
            row.EventSource,
            row.TransactionId,
            It.IsAny<TransactionCompletedEvent>(),
            row.TraceParent,
            row.TraceState,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_when_publish_throws_applies_the_kernel_retry_transition()
    {
        await SeedMessageAsync();
        var publisher = new Mock<IEventPublisher>();
        publisher.Setup(p => p.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport unavailable"));
        using var services = BuildServices(publisher.Object);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = CreateProcessor(services.GetRequiredService<IServiceScopeFactory>(), completion);

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        await using var verifyContext = CreateContext();
        var row = await verifyContext.MessagingOutboxMessages
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        row.Status.Should().Be(MessageConstants.Status.Pending);
        row.RetryCount.Should().Be(1);
        row.LastError.Should().Be("transport unavailable");
    }

    [Theory]
    [InlineData("unsupported.event", "Unsupported messaging outbox event type 'unsupported.event'.")]
    [InlineData(Constants.BalanceUpdated, "is missing NewBalance.")]
    public async Task StartAsync_when_mapping_fails_applies_the_kernel_retry_transition(
        string eventType,
        string expectedError)
    {
        await SeedMessageAsync(eventType);
        var publisher = new Mock<IEventPublisher>(MockBehavior.Strict);
        using var services = BuildServices(publisher.Object);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = CreateProcessor(services.GetRequiredService<IServiceScopeFactory>(), completion);

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        await using var verifyContext = CreateContext();
        var row = await verifyContext.MessagingOutboxMessages
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        row.Status.Should().Be(MessageConstants.Status.Pending);
        row.RetryCount.Should().Be(1);
        row.LastError.Should().Contain(expectedError);
        publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public void Concrete_processor_overrides_only_the_lock_name_prefix_and_store_name()
    {
        var declaredMethods = typeof(MessagingOutboxProcessor)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        // Story 6.5 adds a second override (StoreName) alongside the
        // existing LockNamePrefix — everything else (polling, partition
        // fan-out, locking, claiming, retry/terminal-failure classification)
        // still stays owned by the base class.
        declaredMethods.Select(m => m.Name).Should().BeEquivalentTo(["get_LockNamePrefix", "get_StoreName"]);
    }

    private ServiceProvider BuildServices(IEventPublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider);
        services.AddSingleton(TestBusinessMetrics.Instance);
        services.AddSingleton(publisher);
        services.AddScoped<CoreBankDbContext>(_ => CreateContext());
        services.AddScoped<MessagingOutboxRepository>();
        services.AddScoped<IOutboxMessageStore<MessagingOutboxMessage>>(
            sp => sp.GetRequiredService<MessagingOutboxRepository>());
        services.AddScoped<IOutboxDeliveryStrategy<MessagingOutboxMessage>, DaprOutboxDeliveryStrategy>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private MessagingOutboxProcessor CreateProcessor(
        IServiceScopeFactory scopeFactory,
        TaskCompletionSource completion,
        SingleTickLockService? lockService = null) =>
        new(
            lockService ?? new SingleTickLockService(completion),
            scopeFactory,
            new ActivitySource(nameof(MessagingOutboxProcessorTests)),
            TimeProvider,
            NullLogger<MessagingOutboxProcessor>.Instance,
            TestBusinessMetrics.Instance,
            Options.Create(new MessagingOutboxProcessingOptions
            {
                PartitionCount = 1,
                LockExpirySeconds = 30,
                PollingIntervalMs = 60000
            }));

    private async Task SeedMessageAsync(string eventType = Constants.TransactionCompleted)
    {
        await using var context = CreateContext();
        context.MessagingOutboxMessages.Add(new MessagingOutboxMessage
        {
            Id = Guid.NewGuid(),
            PartitionId = 0,
            IdempotencyKey = "txn-123",
            Status = MessageConstants.Status.Pending,
            CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
            EventOccurredAt = TimeProvider.GetUtcNow().UtcDateTime,
            TransactionId = "txn-123",
            EventType = eventType,
            EventSource = "https://corebank-api/transactions",
            AccountNumber = "NL91ABNA0417164300",
            ToAccount = "NL20INGB0001234567",
            Amount = 50m,
            Currency = "EUR",
            TransactionStatus = MessageConstants.Status.Completed,
            TraceParent = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01"
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private sealed class SingleTickLockService(TaskCompletionSource completion) : IDistributedLockService
    {
        private int _executed;
        public string? LockName { get; private set; }

        public async Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            LockName = lockName;
            if (Interlocked.Exchange(ref _executed, 1) != 0)
            {
                return false;
            }

            await workload(cancellationToken);
            completion.TrySetResult();
            return true;
        }
    }
}
