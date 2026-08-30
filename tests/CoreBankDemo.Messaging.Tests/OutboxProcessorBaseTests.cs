using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CoreBankDemo.Messaging.Tests;

/// <summary>
/// Moq-tier unit tests for <see cref="OutboxProcessorBase{TMessage}"/> (story
/// 2.4), covering the full I/O matrix off mocks of all four dependencies
/// (<see cref="IOutboxMessageStore{TMessage}"/>, <see cref="IDistributedLockService"/>,
/// <see cref="IOutboxDeliveryStrategy{TMessage}"/>, plus a real
/// <see cref="ActivitySource"/>/<see cref="TimeProvider"/>/<see cref="ILogger"/>)
/// — no database, no hosted-service lifecycle.
///
/// <para>
/// Test-seam choice: <see cref="OutboxProcessorBase{TMessage}"/> exposes its
/// per-tick logic as <c>internal Task RunTickAsync(CancellationToken)</c>
/// (visible here via <c>InternalsVisibleTo</c> on the production project)
/// rather than only through the <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
/// base's <c>ExecuteAsync</c>/<c>StartAsync</c>/<c>StopAsync</c> lifecycle.
/// That lets every test below invoke exactly one tick, synchronously await it,
/// and assert on it directly — no polling-interval delays, no starting/stopping
/// a host, no racing a background loop to catch it mid-tick. The
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> loop itself
/// (poll → tick → delay → repeat, cancel to stop) is exercised separately in
/// <see cref="ExecuteAsync_runs_ticks_in_a_loop_until_cancelled"/> via the real
/// hosted-service lifecycle, so that shape isn't left untested — it's just not
/// how every scenario in the I/O matrix is verified.
/// </para>
/// </summary>
public class OutboxProcessorBaseTests
{
    private static readonly ActivitySource ActivitySource = new(nameof(OutboxProcessorBaseTests));
    private static readonly BusinessMetrics TestBusinessMetrics = new();

    private sealed class TestOutboxProcessor : OutboxProcessorBase<TestOutboxEventMessage>
    {
        public TestOutboxProcessor(
            IOutboxMessageStore<TestOutboxEventMessage> store,
            IDistributedLockService lockService,
            IOutboxDeliveryStrategy<TestOutboxEventMessage> deliveryStrategy,
            ActivitySource activitySource,
            TimeProvider timeProvider,
            ILogger logger,
            BusinessMetrics businessMetrics,
            OutboxProcessorOptions? options = null)
            : this(
                lockService,
                new FixedScopeFactory(store, deliveryStrategy),
                activitySource,
                timeProvider,
                logger,
                businessMetrics,
                options)
        {
        }

        public TestOutboxProcessor(
            IDistributedLockService lockService,
            IServiceScopeFactory scopeFactory,
            ActivitySource activitySource,
            TimeProvider timeProvider,
            ILogger logger,
            BusinessMetrics businessMetrics,
            OutboxProcessorOptions? options = null)
            : base(lockService, scopeFactory, activitySource, timeProvider, logger, businessMetrics, options)
        {
        }

        protected override BusinessMetrics.StoreName StoreName => BusinessMetrics.StoreName.PaymentsOutbox;

        protected override string LockNamePrefix => "test-outbox";
    }

    private sealed class FixedScopeFactory(
        IOutboxMessageStore<TestOutboxEventMessage> store,
        IOutboxDeliveryStrategy<TestOutboxEventMessage> deliveryStrategy) : IServiceScopeFactory
    {
        public int CreateCount { get; private set; }

        public IServiceScope CreateScope()
        {
            CreateCount++;
            return new FixedScope(new FixedServiceProvider(store, deliveryStrategy));
        }
    }

    private sealed class FixedScope(IServiceProvider serviceProvider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public void Dispose() { }
    }

    private sealed class FixedServiceProvider(
        IOutboxMessageStore<TestOutboxEventMessage> store,
        IOutboxDeliveryStrategy<TestOutboxEventMessage> deliveryStrategy) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IOutboxMessageStore<TestOutboxEventMessage>)
                ? store
                : serviceType == typeof(IOutboxDeliveryStrategy<TestOutboxEventMessage>)
                    ? deliveryStrategy
                    : null;
    }

    private sealed class ScopedStore : IOutboxMessageStore<TestOutboxEventMessage>, IAsyncDisposable
    {
        public static ConcurrentBag<ScopedStore> Instances { get; } = new();
        public List<int> ClaimedPartitions { get; } = new();
        public bool IsDisposed { get; private set; }

        public ScopedStore() => Instances.Add(this);

        public Task<IReadOnlyList<TestOutboxEventMessage>> ClaimBatchForPartitionAsync(
            int partitionId,
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            ClaimedPartitions.Add(partitionId);
            return Task.FromResult<IReadOnlyList<TestOutboxEventMessage>>(Array.Empty<TestOutboxEventMessage>());
        }

        public Task<MessageTransitionOutcome> MarkAsCompletedAsync(
            TestOutboxEventMessage message,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MessageTransitionOutcome.Applied);

        public Task<MessageTransitionOutcome> MarkAsFailedWithRetryAsync(
            TestOutboxEventMessage message,
            string errorMessage,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MessageTransitionOutcome.Applied);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScopedStrategy : IOutboxDeliveryStrategy<TestOutboxEventMessage>, IAsyncDisposable
    {
        public static ConcurrentBag<ScopedStrategy> Instances { get; } = new();
        public bool IsDisposed { get; private set; }

        public ScopedStrategy() => Instances.Add(this);

        public Task DeliverAsync(
            TestOutboxEventMessage message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Passthrough fake: every lock is always acquired, workload runs inline.</summary>
    private sealed class AlwaysAcquiringLockService : IDistributedLockService
    {
        public List<(string LockName, int LockExpirySeconds)> Calls { get; } = new();

        public async Task<bool> ExecuteWithLockAsync(
            string lockName, int lockExpirySeconds, Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((lockName, lockExpirySeconds));
            await workload(cancellationToken);
            return true;
        }
    }

    /// <summary>Fake that never acquires any lock — the workload must never run.</summary>
    private sealed class NeverAcquiringLockService : IDistributedLockService
    {
        public Task<bool> ExecuteWithLockAsync(
            string lockName, int lockExpirySeconds, Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    /// <summary>
    /// Real serialization by lock name: a lock already "held" is refused
    /// (returns false) rather than queued, mirroring a real distributed lock
    /// under contention. Tracks whether two callers were ever inside the same
    /// lock name's workload simultaneously.
    /// </summary>
    private sealed class SerializingLockService : IDistributedLockService
    {
        private readonly ConcurrentDictionary<string, bool> _held = new();
        public bool ConcurrentExecutionDetected { get; private set; }
        public int WorkloadInvocations;

        public async Task<bool> ExecuteWithLockAsync(
            string lockName, int lockExpirySeconds, Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            if (!_held.TryAdd(lockName, true))
            {
                return false;
            }

            try
            {
                Interlocked.Increment(ref WorkloadInvocations);
                await workload(cancellationToken);
            }
            finally
            {
                if (!_held.TryRemove(lockName, out _))
                {
                    ConcurrentExecutionDetected = true;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Fake that throws for one designated partition — either synchronously
    /// (before returning any <see cref="Task"/> at all, e.g. mimicking eager
    /// argument validation a real lock-service implementation might do) or
    /// asynchronously (a properly awaited throw, e.g. mimicking a failed
    /// remote call) depending on <c>throwSynchronously</c> — and behaves like
    /// <see cref="AlwaysAcquiringLockService"/> (lock always acquired,
    /// workload runs inline) for every other partition. Proves
    /// partition-level isolation holds for both flavors of "the lock service
    /// throws instead of just returning false" (story 2.6): a naive
    /// <c>Task.WhenAll</c>-based fan-out that eagerly enumerates a
    /// <c>Select(...).ToArray()</c> of per-partition calls would have a
    /// synchronous throw from one partition abort the enumeration itself,
    /// silently skipping every partition after it — the synchronous variant
    /// exists specifically to catch that class of bug.
    /// </summary>
    private sealed class SelectivelyThrowingLockService : IDistributedLockService
    {
        private readonly int _throwingPartitionId;
        private readonly Exception _exception;
        private readonly bool _throwSynchronously;
        private readonly ConcurrentBag<int> _attemptedPartitions = new();

        public SelectivelyThrowingLockService(int throwingPartitionId, Exception exception, bool throwSynchronously)
        {
            _throwingPartitionId = throwingPartitionId;
            _exception = exception;
            _throwSynchronously = throwSynchronously;
        }

        public IReadOnlyCollection<int> AttemptedPartitions => _attemptedPartitions;

        public Task<bool> ExecuteWithLockAsync(
            string lockName, int lockExpirySeconds, Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            var partitionId = int.Parse(lockName[(lockName.LastIndexOf('-') + 1)..]);
            _attemptedPartitions.Add(partitionId);

            if (partitionId == _throwingPartitionId && _throwSynchronously)
            {
                throw _exception;
            }

            return RunAsync(partitionId, workload, cancellationToken);
        }

        private async Task<bool> RunAsync(int partitionId, Func<CancellationToken, Task> workload, CancellationToken cancellationToken)
        {
            if (partitionId == _throwingPartitionId)
            {
                throw _exception;
            }

            await workload(cancellationToken);
            return true;
        }
    }

    /// <summary>
    /// Fake mirroring the real lock service's shape (see
    /// <c>DaprDistributedLockService</c>'s 5/6-lock-lifetime <c>workCts</c>):
    /// it hands the workload a <see cref="CancellationToken"/> it owns and
    /// controls itself — distinct from, and cancelled independently of, the
    /// ambient token the caller passed to <see cref="ExecuteWithLockAsync"/>.
    /// Lets a test prove the dispatch loop stops promptly on whichever token
    /// the lock workload was actually handed — the seam epic 3's real
    /// 5/6-lifetime cancellation will drive — rather than only ever being
    /// exercised via the ambient <c>stoppingToken</c>.
    /// </summary>
    private sealed class LockSuppliedCancellationLockService : IDistributedLockService
    {
        private readonly CancellationTokenSource _lockOwnedCts = new();

        public void CancelLockOwnedToken() => _lockOwnedCts.Cancel();

        public Task<bool> ExecuteWithLockAsync(
            string lockName, int lockExpirySeconds, Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default) => RunAsync(workload);

        private async Task<bool> RunAsync(Func<CancellationToken, Task> workload)
        {
            await workload(_lockOwnedCts.Token);
            return true;
        }
    }

    private static TestOutboxEventMessage NewMessage(string key = "msg", int partitionId = 0) => new()
    {
        IdempotencyKey = key,
        EventType = "Debited",
        PartitionId = partitionId,
        Status = MessageConstants.Status.Processing,
        CreatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task Tick_resolves_and_disposes_a_distinct_store_and_strategy_scope_per_partition()
    {
        while (ScopedStore.Instances.TryTake(out _)) { }
        while (ScopedStrategy.Instances.TryTake(out _)) { }

        using var services = new ServiceCollection()
            .AddScoped<IOutboxMessageStore<TestOutboxEventMessage>, ScopedStore>()
            .AddScoped<IOutboxDeliveryStrategy<TestOutboxEventMessage>, ScopedStrategy>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var processor = new TestOutboxProcessor(
            new AlwaysAcquiringLockService(),
            services.GetRequiredService<IServiceScopeFactory>(),
            ActivitySource,
            TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 4 });

        await processor.RunTickAsync(CancellationToken.None);

        ScopedStore.Instances.Should().HaveCount(4);
        ScopedStore.Instances.SelectMany(store => store.ClaimedPartitions)
            .Should().BeEquivalentTo(new[] { 0, 1, 2, 3 });
        ScopedStore.Instances.Should().OnlyHaveUniqueItems();
        ScopedStore.Instances.Should().AllSatisfy(store => store.IsDisposed.Should().BeTrue());
        ScopedStrategy.Instances.Should().HaveCount(4);
        ScopedStrategy.Instances.Should().OnlyHaveUniqueItems();
        ScopedStrategy.Instances.Should().AllSatisfy(strategy => strategy.IsDisposed.Should().BeTrue());
    }

    [Fact]
    public async Task Tick_fans_out_over_every_partition_under_its_own_lock_name()
    {
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        var lockService = new AlwaysAcquiringLockService();
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        var options = new OutboxProcessorOptions { PartitionCount = 3 };
        var processor = new TestOutboxProcessor(
            store.Object, lockService, strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, options);

        await processor.RunTickAsync(CancellationToken.None);

        lockService.Calls.Select(c => c.LockName).Should().BeEquivalentTo(
            "test-outbox-partition-0", "test-outbox-partition-1", "test-outbox-partition-2");
        store.Verify(s => s.ClaimBatchForPartitionAsync(0, MessageConstants.Defaults.BatchSize, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.ClaimBatchForPartitionAsync(1, MessageConstants.Defaults.BatchSize, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.ClaimBatchForPartitionAsync(2, MessageConstants.Defaults.BatchSize, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Lock_not_acquired_skips_the_partition_silently_without_throwing()
    {
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        var lockService = new NeverAcquiringLockService();
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        var scopeFactory = new FixedScopeFactory(store.Object, strategy.Object);
        var processor = new TestOutboxProcessor(
            lockService, scopeFactory, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        var act = async () => await processor.RunTickAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        scopeFactory.CreateCount.Should().Be(0);
        store.Verify(s => s.ClaimBatchForPartitionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Full_tick_where_every_partition_fails_to_acquire_its_lock_completes_with_zero_dispatch_and_no_throw()
    {
        // Story 2.6: the whole tick, not just a single partition, must
        // survive a lock service that never grants any of the four
        // partitions its lock — no work happens anywhere, and no exception
        // escapes the tick.
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        var lockService = new NeverAcquiringLockService();
        var processor = new TestOutboxProcessor(
            store.Object, lockService, strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 4 });

        var act = async () => await processor.RunTickAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("a full tick where every partition fails to acquire its lock must not throw");
        store.Verify(s => s.ClaimBatchForPartitionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            "no partition acquired its lock, so no claim/dispatch work may happen anywhere in the tick");
        strategy.Verify(s => s.DeliverAsync(It.IsAny<TestOutboxEventMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delivery_success_marks_the_message_completed()
    {
        var message = NewMessage();
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { message });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        await processor.RunTickAsync(CancellationToken.None);

        store.Verify(s => s.MarkAsCompletedAsync(message, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.MarkAsFailedWithRetryAsync(It.IsAny<TestOutboxEventMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delivery_failure_marks_the_message_failed_with_retry_and_does_not_escape_the_tick()
    {
        var message = NewMessage();
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { message });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("downstream refused it"));
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        var act = async () => await processor.RunTickAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("a delivery exception must never escape the tick");
        store.Verify(s => s.MarkAsFailedWithRetryAsync(message, "downstream refused it", It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.MarkAsCompletedAsync(It.IsAny<TestOutboxEventMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Completion_persistence_failure_after_successful_delivery_is_not_misclassified_as_a_delivery_failure()
    {
        // The real bug this guards: DeliverAsync succeeding and then
        // MarkAsCompletedAsync throwing (e.g. a DbUpdateConcurrencyException)
        // must NOT be reported as a delivery failure and must NOT burn a
        // RetryCount via MarkAsFailedWithRetryAsync — that would flip an
        // already-delivered message back to Pending and redeliver it.
        var message = NewMessage();
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { message });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        store.Setup(s => s.MarkAsCompletedAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("concurrency conflict persisting completion"));
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var logger = new Mock<ILogger>();
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            logger.Object, TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        var act = async () => await processor.RunTickAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("a completion-persistence exception must never escape the tick either");
        store.Verify(s => s.MarkAsFailedWithRetryAsync(It.IsAny<TestOutboxEventMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "a bookkeeping failure after successful delivery must never burn a RetryCount or flip the message back to Pending");
        strategy.Verify(s => s.DeliverAsync(message, It.IsAny<CancellationToken>()), Times.Once,
            "delivery itself succeeded and must not be retried/re-invoked for a completion-persistence failure");

        // Delivery-failure and completion-failure must produce observably
        // different outcomes: delivery failures log at Warning with "Outbox
        // delivery failed" (see Delivery_failure_logs_a_warning); this path
        // must log a distinct message that does not claim delivery failed.
        logger.Verify(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Outbox delivery failed")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never,
            "a completion-persistence failure must not be logged as a delivery failure");
        logger.Verify(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("after successful delivery")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "the completion-persistence failure must be logged distinctly from a delivery failure");
    }

    // ---- Story 6.5: business metrics ----

    [Fact]
    public async Task Delivery_success_records_queue_duration_and_a_completed_item_metric()
    {
        var claimedAt = new DateTime(2026, 8, 21, 0, 45, 0, DateTimeKind.Utc);
        var message = NewMessage();
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { message });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, new FixedTimeProvider(claimedAt),
            NullLoggerLike(), businessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        await processor.RunTickAsync(CancellationToken.None);

        listener.Measurements.Should().ContainSingle(m => m.InstrumentName == BusinessMetrics.MessagingQueueDurationInstrumentName)
            .Which.Value.Should().Be(45d * 60 * 1000, "the message waited 45 minutes between CreatedAt and the fixed claimedAt");
        var itemMeasurement = listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.MessagingItemsProcessedInstrumentName).Which;
        itemMeasurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["messaging.store.name"] = "payments-outbox",
            ["messaging.store.kind"] = "outbox",
            ["outcome"] = "completed",
        });
    }

    [Fact]
    public async Task Delivery_failure_below_max_retry_records_a_retry_scheduled_item_metric()
    {
        var message = NewMessage();
        message.RetryCount = 0;
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { message });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        store.Setup(s => s.MarkAsFailedWithRetryAsync(message, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<TestOutboxEventMessage, string, CancellationToken>((m, _, _) => m.Status = MessageConstants.Status.Pending)
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), businessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        await processor.RunTickAsync(CancellationToken.None);

        listener.Measurements.Should().ContainSingle(m => m.InstrumentName == BusinessMetrics.MessagingItemsProcessedInstrumentName)
            .Which.Tags["outcome"].Should().Be("retry_scheduled");
    }

    [Fact]
    public async Task Delivery_failure_at_max_retry_records_a_terminal_failed_item_metric_exactly_once()
    {
        var message = NewMessage();
        message.RetryCount = MessageConstants.Defaults.MaxRetryCount - 1;
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { message });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        store.Setup(s => s.MarkAsFailedWithRetryAsync(message, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<TestOutboxEventMessage, string, CancellationToken>((m, _, _) => m.Status = MessageConstants.Status.Failed)
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), businessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        await processor.RunTickAsync(CancellationToken.None);

        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.MessagingItemsProcessedInstrumentName)
            .Which.Tags["outcome"].Should().Be("terminal_failed");
    }

    [Fact]
    public async Task Completion_persistence_failure_records_a_completion_persistence_failed_item_metric()
    {
        var message = NewMessage();
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { message });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        store.Setup(s => s.MarkAsCompletedAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("concurrency conflict"));
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), businessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        await processor.RunTickAsync(CancellationToken.None);

        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.MessagingItemsProcessedInstrumentName)
            .Which.Tags["outcome"].Should().Be("completion_persistence_failed");
    }

    [Fact]
    public async Task Concurrent_terminal_transition_records_no_completed_metric()
    {
        var message = NewMessage();
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { message });
        store.Setup(s => s.MarkAsCompletedAsync(message, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.AlreadyTerminal);
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(message, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var processor = new TestOutboxProcessor(
            store.Object,
            new AlwaysAcquiringLockService(),
            strategy.Object,
            ActivitySource,
            TimeProvider.System,
            NullLoggerLike(),
            businessMetrics,
            new OutboxProcessorOptions { PartitionCount = 1 });

        await processor.RunTickAsync(CancellationToken.None);

        listener.Measurements.Should().NotContain(
            measurement => measurement.InstrumentName == BusinessMetrics.MessagingItemsProcessedInstrumentName);
    }

    [Fact]
    public async Task Retry_persistence_failure_records_a_retry_persistence_failed_item_metric()
    {
        var message = NewMessage();
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { message });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        store.Setup(s => s.MarkAsFailedWithRetryAsync(message, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient db conflict"));
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), businessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        await processor.RunTickAsync(CancellationToken.None);

        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.MessagingItemsProcessedInstrumentName)
            .Which.Tags["outcome"].Should().Be("retry_persistence_failed");
    }

    [Fact]
    public async Task Lock_not_acquired_records_no_queue_duration_or_item_metric()
    {
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        var lockService = new NeverAcquiringLockService();
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        var scopeFactory = new FixedScopeFactory(store.Object, strategy.Object);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var processor = new TestOutboxProcessor(
            lockService, scopeFactory, ActivitySource, TimeProvider.System,
            NullLoggerLike(), businessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        await processor.RunTickAsync(CancellationToken.None);

        listener.Measurements.Should().BeEmpty();
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    [Fact]
    public async Task Null_claim_batch_from_the_store_is_treated_as_empty_rather_than_throwing()
    {
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)null!);
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        var act = async () => await processor.RunTickAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("a misbehaving store returning null must degrade to an empty batch, not NRE");
        strategy.Verify(s => s.DeliverAsync(It.IsAny<TestOutboxEventMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delivery_failure_on_one_message_still_lets_the_tick_continue_to_the_next_message()
    {
        var first = NewMessage("first");
        var second = NewMessage("second");
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { first, second });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(first, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
        strategy.Setup(s => s.DeliverAsync(second, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        await processor.RunTickAsync(CancellationToken.None);

        store.Verify(s => s.MarkAsFailedWithRetryAsync(first, "boom", It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.MarkAsCompletedAsync(second, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delivery_failure_followed_by_a_MarkAsFailedWithRetryAsync_failure_does_not_escape_the_tick_and_the_next_message_still_dispatches()
    {
        // The real bug this guards: a transient DB conflict while persisting
        // the retry (MarkAsFailedWithRetryAsync itself throwing) must not
        // escape ProcessMessageAsync — that would abort the rest of this
        // partition's batch for the tick, leaving later claimed messages
        // never delivered.
        var first = NewMessage("first");
        var second = NewMessage("second");
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { first, second });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        store.Setup(s => s.MarkAsFailedWithRetryAsync(first, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient DB conflict persisting retry"));
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(first, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        strategy.Setup(s => s.DeliverAsync(second, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var logger = new Mock<ILogger>();
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            logger.Object, TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        var act = async () => await processor.RunTickAsync(CancellationToken.None);

        await act.Should().NotThrowAsync(
            "a MarkAsFailedWithRetryAsync failure after a delivery failure must never escape the tick");
        store.Verify(s => s.MarkAsFailedWithRetryAsync(first, "boom", It.IsAny<CancellationToken>()), Times.Once);
        strategy.Verify(s => s.DeliverAsync(second, It.IsAny<CancellationToken>()), Times.Once,
            "the tick must continue to the next message in the batch despite the retry-persistence failure");
        store.Verify(s => s.MarkAsCompletedAsync(second, It.IsAny<CancellationToken>()), Times.Once);
        logger.Verify(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Failed to record retry")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "the secondary retry-persistence failure must be logged distinctly");
    }

    [Fact]
    public async Task Cancellation_mid_dispatch_stops_promptly_without_completing_or_retrying_the_in_flight_message()
    {
        using var cts = new CancellationTokenSource();
        var inFlight = NewMessage("in-flight");
        var neverReached = NewMessage("never-reached");
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { inFlight, neverReached });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(inFlight, It.IsAny<CancellationToken>()))
            .Returns<TestOutboxEventMessage, CancellationToken>((_, ct) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(ct);
            });
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        var act = async () => await processor.RunTickAsync(cts.Token);

        await act.Should().NotThrowAsync("cancellation is swallowed at the tick boundary like any other tick-level exception");
        strategy.Verify(s => s.DeliverAsync(neverReached, It.IsAny<CancellationToken>()), Times.Never,
            "dispatch must stop promptly on cancellation rather than continuing to the next message");
        store.Verify(s => s.MarkAsCompletedAsync(inFlight, It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.MarkAsFailedWithRetryAsync(inFlight, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "a cancelled in-flight delivery is not a transport failure and must not consume a retry");
    }

    [Fact]
    public async Task Cancellation_via_a_token_supplied_by_the_lock_workload_itself_stops_dispatch_promptly_without_touching_the_ambient_token()
    {
        // Story 2.6: the real lock service (DaprDistributedLockService) hands
        // the workload a token IT derives and owns (5/6-lock-lifetime cutoff)
        // — distinct from the ambient stoppingToken. This proves the
        // dispatch loop honors that lock-supplied token specifically, not
        // merely the ambient token it happens to equal in every other test.
        var inFlight = NewMessage("in-flight");
        var neverReached = NewMessage("never-reached");
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { inFlight, neverReached });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        var lockService = new LockSuppliedCancellationLockService();
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(inFlight, It.IsAny<CancellationToken>()))
            .Returns<TestOutboxEventMessage, CancellationToken>((_, ct) =>
            {
                lockService.CancelLockOwnedToken();
                throw new OperationCanceledException(ct);
            });
        var processor = new TestOutboxProcessor(
            store.Object, lockService, strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        using var ambientCts = new CancellationTokenSource();
        var act = async () => await processor.RunTickAsync(ambientCts.Token);

        await act.Should().NotThrowAsync("cancellation via the lock-supplied token is swallowed at the tick boundary like any other tick-level exception");
        ambientCts.IsCancellationRequested.Should().BeFalse(
            "the ambient token must never be cancelled by this scenario — only the lock-owned token is");
        strategy.Verify(s => s.DeliverAsync(neverReached, It.IsAny<CancellationToken>()), Times.Never,
            "dispatch must stop promptly once the lock-supplied token is cancelled, not continue to the next message");
        store.Verify(s => s.MarkAsCompletedAsync(inFlight, It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.MarkAsFailedWithRetryAsync(inFlight, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "a cancellation via the lock-owned token is not a delivery failure and must not consume a retry");
    }

    [Fact]
    public async Task Cancellation_via_a_token_supplied_by_the_lock_workload_itself_is_never_logged_as_a_lock_service_failure()
    {
        // Story 2.6 review patch: ProcessPartitionUnderLockAsync's catch must
        // not mislabel ordinary cancellation propagating up from
        // ProcessMessageAsync's deliberate rethrow as a distributed-lock
        // backend failure — that would misdirect on-call diagnosis during a
        // real incident. Same scenario as
        // Cancellation_via_a_token_supplied_by_the_lock_workload_itself_stops_dispatch_promptly_without_touching_the_ambient_token,
        // but with a real Mock<ILogger>() so the absence of the "Lock service
        // failed" Error-level log can be asserted directly.
        var inFlight = NewMessage("in-flight");
        var neverReached = NewMessage("never-reached");
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { inFlight, neverReached });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        var lockService = new LockSuppliedCancellationLockService();
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(inFlight, It.IsAny<CancellationToken>()))
            .Returns<TestOutboxEventMessage, CancellationToken>((_, ct) =>
            {
                lockService.CancelLockOwnedToken();
                throw new OperationCanceledException(ct);
            });
        var logger = new Mock<ILogger>();
        var processor = new TestOutboxProcessor(
            store.Object, lockService, strategy.Object, ActivitySource, TimeProvider.System,
            logger.Object, TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        using var ambientCts = new CancellationTokenSource();
        var act = async () => await processor.RunTickAsync(ambientCts.Token);

        await act.Should().NotThrowAsync("cancellation via the lock-supplied token is swallowed at the tick boundary like any other tick-level exception");
        logger.Verify(l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Lock service failed")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never,
            "cancellation via the lock-supplied token must never be mislabeled as a lock service failure");
    }

    [Fact]
    public async Task Lock_service_throwing_asynchronously_for_one_partition_still_lets_the_other_three_process_and_logs_the_exception()
    {
        // Acceptance criterion: given a lock service that throws for one
        // partition among four, the other three still process normally and
        // the exception is logged, not rethrown.
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        var lockService = new SelectivelyThrowingLockService(
            throwingPartitionId: 1, new InvalidOperationException("lock backend unreachable"), throwSynchronously: false);
        var logger = new Mock<ILogger>();
        var processor = new TestOutboxProcessor(
            store.Object, lockService, strategy.Object, ActivitySource, TimeProvider.System,
            logger.Object, TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 4 });

        var act = async () => await processor.RunTickAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("a lock-service exception for one partition must never crash the tick");
        store.Verify(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.ClaimBatchForPartitionAsync(2, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.ClaimBatchForPartitionAsync(3, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.ClaimBatchForPartitionAsync(1, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            "the throwing partition's own claim never runs since the lock service failed before the workload could");
        logger.Verify(l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce, "the lock-service exception must be logged");
    }

    [Fact]
    public async Task Lock_service_throwing_synchronously_for_one_partition_still_lets_every_partition_be_attempted()
    {
        // Hardening variant of the acceptance criterion above: even a
        // badly-behaved lock-service implementation that throws BEFORE
        // returning any Task (e.g. eager argument validation) must not
        // short-circuit the fan-out over the other partitions.
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        var lockService = new SelectivelyThrowingLockService(
            throwingPartitionId: 1, new InvalidOperationException("lock backend unreachable"), throwSynchronously: true);
        var logger = new Mock<ILogger>();
        var processor = new TestOutboxProcessor(
            store.Object, lockService, strategy.Object, ActivitySource, TimeProvider.System,
            logger.Object, TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 4 });

        var act = async () => await processor.RunTickAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("a synchronous lock-service exception for one partition must never crash the tick");
        lockService.AttemptedPartitions.Should().BeEquivalentTo(new[] { 0, 1, 2, 3 },
            "every partition must still be attempted even when an earlier partition's lock call throws synchronously");
        store.Verify(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.ClaimBatchForPartitionAsync(2, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.ClaimBatchForPartitionAsync(3, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        logger.Verify(l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce, "the lock-service exception must be logged");
    }

    [Fact]
    public async Task Tick_level_exception_from_the_store_is_logged_and_swallowed_so_the_tick_survives()
    {
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unreachable"));
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        var logger = new Mock<ILogger>();
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            logger.Object, TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        var act = async () => await processor.RunTickAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        logger.Verify(l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Delivery_failure_logs_a_warning()
    {
        var message = NewMessage();
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { message });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(message, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("nope"));
        var logger = new Mock<ILogger>();
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            logger.Object, TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        await processor.RunTickAsync(CancellationToken.None);

        logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Two_concurrent_ticks_never_run_the_same_partitions_workload_at_the_same_time()
    {
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                // Hold the "workload" open briefly so overlapping ticks have a
                // real chance to collide if the processor ever bypassed the lock.
                await Task.Delay(20);
                return (IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>();
            });
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        var lockService = new SerializingLockService();
        var processor = new TestOutboxProcessor(
            store.Object, lockService, strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 4 });

        await Task.WhenAll(
            processor.RunTickAsync(CancellationToken.None),
            processor.RunTickAsync(CancellationToken.None));

        lockService.ConcurrentExecutionDetected.Should().BeFalse(
            "no partition's workload may run concurrently across two overlapping ticks");
        lockService.WorkloadInvocations.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Dispatch_activity_carries_idempotency_key_and_partition_id_tags()
    {
        var message = NewMessage("tagged-message", partitionId: 2);
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(2, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { message });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 2), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var capturedTags = new List<KeyValuePair<string, object?>>();
        var capturedKinds = new List<ActivityKind>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                capturedTags.AddRange(activity.TagObjects);
                capturedKinds.Add(activity.Kind);
            },
        };
        ActivitySource.AddActivityListener(listener);

        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 4 });

        await processor.RunTickAsync(CancellationToken.None);

        capturedTags.Should().ContainSingle(t => t.Key == "IdempotencyKey" && Equals(t.Value, "tagged-message"));
        capturedTags.Should().ContainSingle(t => t.Key == "PartitionId" && Equals(t.Value, 2));
        capturedKinds.Should().ContainSingle(k => k == ActivityKind.Producer,
            "an outbox message being delivered is producing work on behalf of the trace that created it (AD-8)");
    }

    [Fact]
    public async Task ExecuteAsync_runs_ticks_in_a_loop_until_cancelled()
    {
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        var tickCount = 0;
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref tickCount);
                return (IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>();
            });
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1, PollingInterval = TimeSpan.FromMilliseconds(10) });

        var testCancellationToken = TestContext.Current.CancellationToken;
        using var cts = new CancellationTokenSource();
        await processor.StartAsync(cts.Token);

        // Give the loop enough real time to complete at least a couple of
        // ticks (poll interval 10ms) before stopping it.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (tickCount < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, testCancellationToken);
        }

        await processor.StopAsync(CancellationToken.None);

        tickCount.Should().BeGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// A minimal non-mocked <see cref="ILogger"/> for tests that need a valid
    /// logger but don't assert on log calls — avoids Moq's strict-by-default
    /// unexpected-call noise for the members that matter to those tests.
    /// </summary>
    private static ILogger NullLoggerLike() => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
}
