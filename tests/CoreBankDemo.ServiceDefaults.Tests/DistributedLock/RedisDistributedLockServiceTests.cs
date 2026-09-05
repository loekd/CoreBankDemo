using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults;
using Medallion.Threading;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.DistributedLock;

/// <summary>
/// Story 6.2 (ADR-011): <see cref="RedisDistributedLockService"/> against a
/// mocked <see cref="IRedisDistributedLockFactory"/>/<see cref="IDistributedLock"/>/
/// <see cref="IDistributedSynchronizationHandle"/> — the same seam the
/// design notes call for so this is provable without a real Redis
/// connection. A real-Redis proof of renewal-past-initial-expiry and
/// cross-instance contention lives separately in
/// <see cref="RedisDistributedLockServiceRealRedisTests"/> (opt-in,
/// infrastructure-dependent).
/// </summary>
public class RedisDistributedLockServiceTests
{
    private static (Mock<IRedisDistributedLockFactory> Factory, Mock<ILogger<RedisDistributedLockService>> Logger, RedisDistributedLockService Sut)
        CreateSut()
    {
        var factory = new Mock<IRedisDistributedLockFactory>();
        var logger = new Mock<ILogger<RedisDistributedLockService>>();
        var sut = new RedisDistributedLockService(factory.Object, logger.Object);
        return (factory, logger, sut);
    }

    /// <summary>A handle whose lease is never lost.</summary>
    private static Mock<IDistributedSynchronizationHandle> CreateHandleMock() =>
        CreateHandleMockLosingLease(CancellationToken.None);

    /// <summary>
    /// A handle whose lease-lost signal is <paramref name="handleLostToken"/>.
    /// Deliberately *not* an overload of <see cref="CreateHandleMock()"/>:
    /// xUnit1051 fires whenever any overload takes a <see cref="CancellationToken"/>,
    /// and satisfying it here would thread the test's cancellation token into a
    /// parameter that simulates the lock's own lease-lost signal -- conflating
    /// "the test was cancelled" with "lock ownership was lost", which is the
    /// exact distinction these tests exist to prove.
    /// </summary>
    private static Mock<IDistributedSynchronizationHandle> CreateHandleMockLosingLease(CancellationToken handleLostToken)
    {
        var handle = new Mock<IDistributedSynchronizationHandle>();
        handle.SetupGet(h => h.HandleLostToken).Returns(handleLostToken);
        handle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return handle;
    }

    private static void SetupAcquire(
        Mock<IRedisDistributedLockFactory> factory,
        string redisLockName,
        TimeSpan expiry,
        IDistributedSynchronizationHandle? handle)
    {
        var lockMock = new Mock<IDistributedLock>();
        lockMock.Setup(l => l.TryAcquireAsync(TimeSpan.Zero, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);
        factory.Setup(f => f.CreateLock(redisLockName, expiry)).Returns(lockMock.Object);
    }

    private static void VerifyLogged(Mock<ILogger<RedisDistributedLockService>> logger, LogLevel level, string containing, Times times) =>
        logger.Verify(l => l.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(containing)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

    [Fact]
    public async Task Lock_acquired_workload_succeeds_returns_true_and_disposes_the_handle()
    {
        var (factory, _, sut) = CreateSut();
        var handle = CreateHandleMock();
        SetupAcquire(factory, "corebankdemo:lock:my-lock", TimeSpan.FromSeconds(30), handle.Object);
        var workloadRan = false;

        var result = await sut.ExecuteWithLockAsync("my-lock", 30, _ =>
        {
            workloadRan = true;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        workloadRan.Should().BeTrue();
        handle.Verify(h => h.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Caller_supplied_lock_name_is_namespaced_with_the_application_prefix_before_reaching_the_factory()
    {
        var (factory, _, sut) = CreateSut();
        var handle = CreateHandleMock();
        SetupAcquire(factory, "corebankdemo:lock:payments-outbox-partition-2", TimeSpan.FromSeconds(30), handle.Object);

        await sut.ExecuteWithLockAsync("payments-outbox-partition-2", 30, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        factory.Verify(f => f.CreateLock("corebankdemo:lock:payments-outbox-partition-2", TimeSpan.FromSeconds(30)), Times.Once);
    }

    [Fact]
    public async Task Passes_the_caller_supplied_lockExpirySeconds_through_to_the_factory_as_a_TimeSpan()
    {
        var (factory, _, sut) = CreateSut();
        var handle = CreateHandleMock();
        SetupAcquire(factory, "corebankdemo:lock:expiry-lock", TimeSpan.FromSeconds(77), handle.Object);

        var result = await sut.ExecuteWithLockAsync("expiry-lock", 77, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        factory.Verify(f => f.CreateLock("corebankdemo:lock:expiry-lock", TimeSpan.FromSeconds(77)), Times.Once);
    }

    [Fact]
    public async Task Acquisition_is_attempted_with_a_zero_timeout_so_a_busy_partition_is_never_queued()
    {
        var (factory, _, sut) = CreateSut();
        var lockMock = new Mock<IDistributedLock>();
        lockMock.Setup(l => l.TryAcquireAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDistributedSynchronizationHandle?)null);
        factory.Setup(f => f.CreateLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(lockMock.Object);

        await sut.ExecuteWithLockAsync("busy-lock", 30, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        lockMock.Verify(l => l.TryAcquireAsync(TimeSpan.Zero, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Lock_not_acquired_returns_false_without_running_the_workload_or_throwing()
    {
        var (factory, _, sut) = CreateSut();
        SetupAcquire(factory, "corebankdemo:lock:busy-lock", TimeSpan.FromSeconds(30), handle: null);
        var workloadRan = false;

        var act = async () => await sut.ExecuteWithLockAsync("busy-lock", 30, _ =>
        {
            workloadRan = true;
            return Task.CompletedTask;
        });

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().BeFalse();
        workloadRan.Should().BeFalse();
    }

    [Fact]
    public async Task Lock_not_acquired_is_logged_at_debug_level()
    {
        var (factory, logger, sut) = CreateSut();
        SetupAcquire(factory, "corebankdemo:lock:busy-lock", TimeSpan.FromSeconds(30), handle: null);

        await sut.ExecuteWithLockAsync("busy-lock", 30, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        VerifyLogged(logger, LogLevel.Debug, "Failed to acquire lock", Times.Once());
    }

    [Fact]
    public async Task Acquisition_throwing_is_caught_logged_at_error_level_and_returns_false()
    {
        var (factory, logger, sut) = CreateSut();
        var lockMock = new Mock<IDistributedLock>();
        lockMock.Setup(l => l.TryAcquireAsync(TimeSpan.Zero, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis unreachable"));
        factory.Setup(f => f.CreateLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(lockMock.Object);

        var act = async () => await sut.ExecuteWithLockAsync("boom-lock", 30, _ => Task.CompletedTask);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().BeFalse();
        VerifyLogged(logger, LogLevel.Error, "Failed to acquire or process with lock", Times.Once());
    }

    [Fact]
    public async Task Workloads_own_exception_is_caught_returns_false_and_the_handle_is_still_disposed()
    {
        var (factory, logger, sut) = CreateSut();
        var handle = CreateHandleMock();
        SetupAcquire(factory, "corebankdemo:lock:throwing-workload", TimeSpan.FromSeconds(30), handle.Object);

        var act = async () => await sut.ExecuteWithLockAsync("throwing-workload", 30, _ => throw new InvalidOperationException("workload blew up"));

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().BeFalse();
        handle.Verify(h => h.DisposeAsync(), Times.Once,
            "the handle must be released even when the workload throws");
        VerifyLogged(logger, LogLevel.Error, "Failed to acquire or process with lock", Times.Once());
    }

    [Fact]
    public async Task DisposeAsync_throwing_after_a_successful_workload_is_caught_and_still_returns_false()
    {
        var (factory, logger, sut) = CreateSut();
        var handle = new Mock<IDistributedSynchronizationHandle>();
        handle.SetupGet(h => h.HandleLostToken).Returns(CancellationToken.None);
        handle.Setup(h => h.DisposeAsync()).ThrowsAsync(new InvalidOperationException("release failed"));
        SetupAcquire(factory, "corebankdemo:lock:unlock-boom", TimeSpan.FromSeconds(30), handle.Object);

        var act = async () => await sut.ExecuteWithLockAsync("unlock-boom", 30, _ => Task.CompletedTask);

        var result = await act.Should().NotThrowAsync("ExecuteWithLockAsync itself must never throw, even when releasing the lock fails");
        result.Subject.Should().BeFalse();
        VerifyLogged(logger, LogLevel.Error, "Failed to acquire or process with lock", Times.Once());
    }

    [Fact]
    public async Task HandleLostToken_cancellation_cancels_the_workload_returns_false_and_leaves_the_ambient_token_untouched()
    {
        var (factory, logger, sut) = CreateSut();
        using var handleLostCts = new CancellationTokenSource();
        var handle = CreateHandleMockLosingLease(handleLostCts.Token);
        SetupAcquire(factory, "corebankdemo:lock:slow-lock", TimeSpan.FromSeconds(30), handle.Object);
        using var ambientCts = new CancellationTokenSource();

        var result = await sut.ExecuteWithLockAsync("slow-lock", 30, workToken =>
        {
            handleLostCts.Cancel();
            workToken.IsCancellationRequested.Should().BeTrue();
            throw new OperationCanceledException(workToken);
        }, ambientCts.Token);

        result.Should().BeFalse();
        ambientCts.IsCancellationRequested.Should().BeFalse("losing the Redis lock must never touch the ambient token");
        VerifyLogged(logger, LogLevel.Warning, "ownership was lost", Times.Once());
        VerifyLogged(logger, LogLevel.Error, "Failed to acquire or process with lock", Times.Never());
    }

    [Fact]
    public async Task HandleLostToken_cancellation_still_disposes_the_handle()
    {
        var (factory, _, sut) = CreateSut();
        using var handleLostCts = new CancellationTokenSource();
        var handle = CreateHandleMockLosingLease(handleLostCts.Token);
        SetupAcquire(factory, "corebankdemo:lock:slow-lock-2", TimeSpan.FromSeconds(30), handle.Object);

        await sut.ExecuteWithLockAsync("slow-lock-2", 30, workToken =>
        {
            handleLostCts.Cancel();
            throw new OperationCanceledException(workToken);
        }, TestContext.Current.CancellationToken);

        handle.Verify(h => h.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Ambient_cancellation_during_the_workload_stops_it_releases_the_handle_and_is_not_mistaken_for_lock_loss()
    {
        var (factory, logger, sut) = CreateSut();
        var handle = CreateHandleMock();
        SetupAcquire(factory, "corebankdemo:lock:ambient-cancel-lock", TimeSpan.FromSeconds(30), handle.Object);
        using var ambientCts = new CancellationTokenSource();

        var result = await sut.ExecuteWithLockAsync("ambient-cancel-lock", 30, workToken =>
        {
            ambientCts.Cancel();
            workToken.IsCancellationRequested.Should().BeTrue();
            throw new OperationCanceledException(workToken);
        }, ambientCts.Token);

        result.Should().BeFalse();
        handle.Verify(h => h.DisposeAsync(), Times.Once, "the handle must be released even when the caller cancels");
        VerifyLogged(logger, LogLevel.Warning, "ownership was lost", Times.Never());
        VerifyLogged(logger, LogLevel.Error, "Failed to acquire or process with lock", Times.Never());
    }

    [Fact]
    public async Task Ambient_cancellation_before_acquisition_returns_false_without_an_error_level_log()
    {
        var (factory, logger, sut) = CreateSut();
        var lockMock = new Mock<IDistributedLock>();
        lockMock.Setup(l => l.TryAcquireAsync(TimeSpan.Zero, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        factory.Setup(f => f.CreateLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(lockMock.Object);
        using var ambientCts = new CancellationTokenSource();
        ambientCts.Cancel();

        var result = await sut.ExecuteWithLockAsync("cancelled-before-acquire", 30, _ => Task.CompletedTask, ambientCts.Token);

        result.Should().BeFalse();
        VerifyLogged(logger, LogLevel.Error, "Failed to acquire or process with lock", Times.Never());
    }

    [Fact]
    public async Task Workload_receives_a_token_linked_to_both_the_ambient_token_and_HandleLostToken()
    {
        var (factory, _, sut) = CreateSut();
        var handle = CreateHandleMock();
        SetupAcquire(factory, "corebankdemo:lock:linked-lock", TimeSpan.FromSeconds(30), handle.Object);
        using var ambientCts = new CancellationTokenSource();
        CancellationToken? observedToken = null;

        await sut.ExecuteWithLockAsync("linked-lock", 30, workToken =>
        {
            observedToken = workToken;
            return Task.CompletedTask;
        }, ambientCts.Token);

        observedToken.Should().NotBeNull();
        observedToken!.Value.IsCancellationRequested.Should().BeFalse();
        observedToken.Value.CanBeCanceled.Should().BeTrue();
    }
}
