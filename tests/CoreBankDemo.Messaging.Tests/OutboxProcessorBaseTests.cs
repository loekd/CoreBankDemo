using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults;
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

    private sealed class TestOutboxProcessor : OutboxProcessorBase<TestOutboxEventMessage>
    {
        public TestOutboxProcessor(
            IOutboxMessageStore<TestOutboxEventMessage> store,
            IDistributedLockService lockService,
            IOutboxDeliveryStrategy<TestOutboxEventMessage> deliveryStrategy,
            ActivitySource activitySource,
            TimeProvider timeProvider,
            ILogger logger,
            OutboxProcessorOptions? options = null)
            : base(store, lockService, deliveryStrategy, activitySource, timeProvider, logger, options)
        {
        }

        protected override string LockNamePrefix => "test-outbox";
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

    private static TestOutboxEventMessage NewMessage(string key = "msg", int partitionId = 0) => new()
    {
        IdempotencyKey = key,
        EventType = "Debited",
        PartitionId = partitionId,
        Status = MessageConstants.Status.Processing,
        CreatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc),
    };

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
            NullLoggerLike(), options);

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
        var processor = new TestOutboxProcessor(
            store.Object, lockService, strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), new OutboxProcessorOptions { PartitionCount = 1 });

        var act = async () => await processor.RunTickAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        store.Verify(s => s.ClaimBatchForPartitionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
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
            NullLoggerLike(), new OutboxProcessorOptions { PartitionCount = 1 });

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
            NullLoggerLike(), new OutboxProcessorOptions { PartitionCount = 1 });

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
            logger.Object, new OutboxProcessorOptions { PartitionCount = 1 });

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

    [Fact]
    public async Task Null_claim_batch_from_the_store_is_treated_as_empty_rather_than_throwing()
    {
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)null!);
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), new OutboxProcessorOptions { PartitionCount = 1 });

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
            NullLoggerLike(), new OutboxProcessorOptions { PartitionCount = 1 });

        await processor.RunTickAsync(CancellationToken.None);

        store.Verify(s => s.MarkAsFailedWithRetryAsync(first, "boom", It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.MarkAsCompletedAsync(second, It.IsAny<CancellationToken>()), Times.Once);
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
            NullLoggerLike(), new OutboxProcessorOptions { PartitionCount = 1 });

        var act = async () => await processor.RunTickAsync(cts.Token);

        await act.Should().NotThrowAsync("cancellation is swallowed at the tick boundary like any other tick-level exception");
        strategy.Verify(s => s.DeliverAsync(neverReached, It.IsAny<CancellationToken>()), Times.Never,
            "dispatch must stop promptly on cancellation rather than continuing to the next message");
        store.Verify(s => s.MarkAsCompletedAsync(inFlight, It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.MarkAsFailedWithRetryAsync(inFlight, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "a cancelled in-flight delivery is not a transport failure and must not consume a retry");
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
            logger.Object, new OutboxProcessorOptions { PartitionCount = 1 });

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
            logger.Object, new OutboxProcessorOptions { PartitionCount = 1 });

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
            NullLoggerLike(), new OutboxProcessorOptions { PartitionCount = 4 });

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
            NullLoggerLike(), new OutboxProcessorOptions { PartitionCount = 4 });

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
            NullLoggerLike(),
            new OutboxProcessorOptions { PartitionCount = 1, PollingInterval = TimeSpan.FromMilliseconds(10) });

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
