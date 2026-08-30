using System.Diagnostics;
using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

/// <summary>
/// Story 5.6's specialization/integration proof for
/// <see cref="InboxProcessor"/>: reuses the read-only
/// <see cref="InboxProcessorBase{TMessage}"/> kernel (generic fan-out,
/// locking, claiming, retry/poison mechanics are
/// <c>CoreBankDemo.Messaging.Tests.InboxProcessorBaseTests</c>'s concern, not
/// re-proven here) against the real <see cref="InboxMessageRepository"/> and
/// <see cref="TransactionEventHandler"/>, proving the whole PaymentsAPI
/// composition end-to-end via the actual <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
/// lifecycle: a claimed row reaches <c>Completed</c> via the kernel's own
/// completion path with no local business-state mutation, a handler failure
/// reaches <c>Pending</c> with an incremented retry count and recorded
/// error, the exact <c>"payments-inbox"</c> lock-name prefix is used, and
/// two different partitions are locked and processed independently.
/// </summary>
public class InboxProcessorTests(PostgresContainerFixture fixture) : PaymentsPostgresTestBase(fixture)
{
    [Fact]
    public async Task StartAsync_claims_dispatches_and_completes_a_valid_event_without_mutating_business_state()
    {
        await using var store = CreateStore();
        await using var seedContext = store.CreateContext();
        var message = NewMessage("txn-completed", Constants.TransactionCompleted, "p0");
        seedContext.InboxMessages.Add(message);
        await seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var services = BuildHandlerServices(store);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = CreateProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            new CountingLockService(expectedLockCount: 1, completion));

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        await using var verifyContext = store.CreateContext();
        var persisted = await verifyContext.InboxMessages
            .AsNoTracking()
            .SingleAsync(m => m.TransactionId == "txn-completed", TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(MessageConstants.Status.Completed);
        persisted.ProcessedAt.Should().NotBeNull();
        persisted.RetryCount.Should().Be(0);
        // Observational only: the stored payload itself is never rewritten.
        persisted.Payload.Should().Be(message.Payload);
    }

    [Theory]
    [InlineData("com.corebank.unsupported.type", """{"transactionId":"txn","status":"Completed","processedAt":"2026-08-29T12:00:00+00:00"}""")]
    [InlineData(Constants.TransactionCompleted, "{not-json")]
    [InlineData(Constants.TransactionCompleted, "null")]
    public async Task StartAsync_when_the_handler_throws_returns_the_row_to_pending_with_a_recorded_error_and_incremented_retry(
        string eventType,
        string payload)
    {
        await using var store = CreateStore();
        await using var seedContext = store.CreateContext();
        var message = NewMessage("txn-handler-error", eventType, "p0", payload: payload);
        seedContext.InboxMessages.Add(message);
        await seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var services = BuildHandlerServices(store);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = CreateProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            new CountingLockService(expectedLockCount: 1, completion));

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        await using var verifyContext = store.CreateContext();
        var persisted = await verifyContext.InboxMessages
            .AsNoTracking()
            .SingleAsync(m => m.TransactionId == "txn-handler-error", TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(MessageConstants.Status.Pending);
        persisted.RetryCount.Should().Be(1);
        persisted.LastError.Should().NotBeNullOrWhiteSpace();
        persisted.ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_restores_the_stored_trace_parent_before_dispatching_the_handler()
    {
        await using var store = CreateStore();
        await using var seedContext = store.CreateContext();
        seedContext.InboxMessages.Add(NewMessage("txn-traced", Constants.TransactionCompleted, "p0"));
        await seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var observedActivity = new TaskCompletionSource<ObservedActivity>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var services = BuildHandlerServices(
            store,
            collection => collection.AddScoped<IInboxMessageHandler<InboxMessage>>(
                _ => new TraceCapturingHandler(
                    new TransactionEventHandler(NullLogger<TransactionEventHandler>.Instance),
                    observedActivity)));
        using var activitySource = new ActivitySource(nameof(InboxProcessorTests));
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == activitySource.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = CreateProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            new CountingLockService(expectedLockCount: 1, completion),
            activitySource: activitySource);

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        var activity = await observedActivity.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        activity.Context.TraceId.Should().Be(ActivityTraceId.CreateFromString("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        activity.ParentSpanId.Should().Be(ActivitySpanId.CreateFromString("bbbbbbbbbbbbbbbb"));
        activity.Context.TraceState.Should().Be("congo=t61rcWkgMzE");
        activity.Kind.Should().Be(ActivityKind.Consumer);
        activity.Tags.Should().Contain(
            new KeyValuePair<string, object?>("event.type", Constants.TransactionCompleted));
        activity.Tags.Should().Contain(
            new KeyValuePair<string, object?>("transaction.status", "Completed"));
    }

    [Fact]
    public async Task StartAsync_at_the_retry_limit_marks_an_unsupported_event_terminally_failed()
    {
        await using var store = CreateStore();
        await using var seedContext = store.CreateContext();
        var message = NewMessage("txn-poison", "com.corebank.unsupported.type", "p0");
        message.RetryCount = MessageConstants.Defaults.MaxRetryCount - 1;
        seedContext.InboxMessages.Add(message);
        await seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var services = BuildHandlerServices(store);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = CreateProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            new CountingLockService(expectedLockCount: 1, completion));

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        await using var verifyContext = store.CreateContext();
        var persisted = await verifyContext.InboxMessages
            .AsNoTracking()
            .SingleAsync(m => m.TransactionId == "txn-poison", TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(MessageConstants.Status.Failed);
        persisted.RetryCount.Should().Be(MessageConstants.Defaults.MaxRetryCount);
        persisted.ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task Each_tick_locks_its_single_partition_using_the_payments_inbox_prefix()
    {
        await using var store = CreateStore();
        using var services = BuildHandlerServices(store);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockService = new CountingLockService(expectedLockCount: 1, completion);
        var processor = CreateProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            lockService,
            partitionCount: 1);

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        lockService.LockNames.Should().ContainSingle(name => name == "payments-inbox-partition-0");
    }

    [Fact]
    public async Task Configured_lock_expiry_and_polling_interval_are_mapped_to_the_kernel()
    {
        await using var store = CreateStore();
        using var services = BuildHandlerServices(store);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockService = new CountingLockService(expectedLockCount: 2, completion);
        var processor = CreateProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            lockService,
            lockExpirySeconds: 17,
            pollingIntervalMs: 10);

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        lockService.LockExpirySeconds.Should().HaveCountGreaterThanOrEqualTo(2);
        lockService.LockExpirySeconds.Should().OnlyContain(expiry => expiry == 17);
    }

    [Fact]
    public async Task Two_different_partitions_are_locked_and_processed_independently()
    {
        await using var store = CreateStore();
        await using var seedContext = store.CreateContext();
        var messageInPartitionZero = NewMessage("txn-p0", Constants.TransactionCompleted, "p0", partitionId: 0);
        var messageInPartitionOne = NewMessage("txn-p1", Constants.TransactionCompleted, "p1", partitionId: 1);
        seedContext.InboxMessages.AddRange(messageInPartitionZero, messageInPartitionOne);
        await seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var services = BuildHandlerServices(store);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockService = new CountingLockService(expectedLockCount: 2, completion);
        var processor = CreateProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            lockService,
            partitionCount: 2);

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        lockService.LockNames.Should().BeEquivalentTo(
            ["payments-inbox-partition-0", "payments-inbox-partition-1"]);
        await using var verifyContext = store.CreateContext();
        var completedCount = await verifyContext.InboxMessages
            .CountAsync(m => m.Status == MessageConstants.Status.Completed, TestContext.Current.CancellationToken);
        completedCount.Should().Be(2);
    }

    private static InboxProcessor CreateProcessor(
        IServiceScopeFactory scopeFactory,
        IDistributedLockService lockService,
        int partitionCount = 1,
        ActivitySource? activitySource = null,
        int lockExpirySeconds = 30,
        int pollingIntervalMs = 60000) =>
        new(
            lockService,
            scopeFactory,
            activitySource ?? new ActivitySource(nameof(InboxProcessorTests)),
            System.TimeProvider.System,
            NullLogger<InboxProcessor>.Instance,
            Options.Create(new InboxProcessingOptions
            {
                PartitionCount = partitionCount,
                LockExpirySeconds = lockExpirySeconds,
                PollingIntervalMs = pollingIntervalMs
            }));

    /// <summary>
    /// Registers the real <see cref="InboxMessageRepository"/> as the
    /// scoped <see cref="IInboxMessageStore{TMessage}"/> the kernel now
    /// resolves per partition (never a ctor-injected field) -- each scope
    /// gets its own fresh <see cref="PaymentsDbContext"/> from
    /// <paramref name="store"/>, exactly mirroring
    /// <c>TransactionEventIntakeServiceCollectionExtensions</c>'s production
    /// registration -- plus the scoped <see cref="TransactionEventHandler"/>
    /// resolved per message.
    /// </summary>
    private static ServiceProvider BuildHandlerServices(
        PaymentsStore store,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<TransactionEventHandler>>(NullLogger<TransactionEventHandler>.Instance);
        services.AddSingleton<TimeProvider>(System.TimeProvider.System);
        services.AddScoped<PaymentsDbContext>(_ => store.CreateContext());
        services.AddScoped<InboxMessageRepository>();
        services.AddScoped<IInboxMessageStore<InboxMessage>>(sp => sp.GetRequiredService<InboxMessageRepository>());
        services.AddScoped<IInboxMessageHandler<InboxMessage>, TransactionEventHandler>();
        configure?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static InboxMessage NewMessage(
        string transactionId,
        string eventType,
        string idempotencyKeySuffix,
        int partitionId = 0,
        string payload = """{"transactionId":"txn","status":"Completed","processedAt":"2026-08-29T12:00:00+00:00"}""") => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = $"{transactionId}-{idempotencyKeySuffix}",
        TransactionId = transactionId,
        EventType = eventType,
        AccountNumber = "",
        Payload = payload,
        PartitionId = partitionId,
        Status = MessageConstants.Status.Pending,
        ReceivedAt = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc),
        TraceParent = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01",
        TraceState = "congo=t61rcWkgMzE"
    };

    /// <summary>
    /// Records every lock name this tick's partition fan-out acquires and
    /// signals <paramref name="completion"/> once <paramref name="expectedLockCount"/>
    /// locks have run their workload -- letting each test wait for exactly
    /// one full tick (across however many partitions it configures) before
    /// stopping the processor, without depending on the real 60s polling
    /// interval ever elapsing.
    /// </summary>
    private sealed class CountingLockService(int expectedLockCount, TaskCompletionSource completion)
        : IDistributedLockService
    {
        private int _count;

        public List<string> LockNames { get; } = [];
        public List<int> LockExpirySeconds { get; } = [];

        public async Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            lock (LockNames)
            {
                LockNames.Add(lockName);
                LockExpirySeconds.Add(lockExpirySeconds);
            }

            await workload(cancellationToken);

            if (Interlocked.Increment(ref _count) == expectedLockCount)
            {
                completion.TrySetResult();
            }

            return true;
        }
    }

    private sealed class TraceCapturingHandler(
        IInboxMessageHandler<InboxMessage> inner,
        TaskCompletionSource<ObservedActivity> observedActivity)
        : IInboxMessageHandler<InboxMessage>
    {
        public async Task HandleAsync(InboxMessage message, CancellationToken cancellationToken = default)
        {
            await inner.HandleAsync(message, cancellationToken);
            var activity = Activity.Current;
            observedActivity.TrySetResult(new ObservedActivity(
                activity?.Context ?? default,
                activity?.ParentSpanId ?? default,
                activity?.Kind ?? default,
                activity?.TagObjects.ToArray() ?? []));
        }
    }

    private sealed record ObservedActivity(
        ActivityContext Context,
        ActivitySpanId ParentSpanId,
        ActivityKind Kind,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);
}
