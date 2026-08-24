#pragma warning disable DAPR_DISTRIBUTEDLOCK
// DaprClient's Lock/Unlock distributed-lock API (and its response types) carry
// Dapr's own "evaluation purposes only" diagnostic. DaprDistributedLockService
// is the one and only place in the codebase allowed to touch them (AD-6); this
// test file is its direct exercise, so the same suppression is warranted here.

using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults;
using Dapr.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.DistributedLock;

/// <summary>
/// Story 3.2: <see cref="DaprDistributedLockService"/> against a mocked
/// <see cref="DaprClient"/> (abstract with virtual <c>Lock</c>/<c>Unlock</c> —
/// directly Moq-mockable, no wrapper seam needed) and a
/// <see cref="FakeTimeProvider"/> for the 5/6-cooperative-cancellation cutoff.
/// Preserves the legacy behavioral reference exactly: hardcoded lock store
/// <c>"lockstore"</c>, owner token <c>"{MachineName}-{Guid}"</c>, failed
/// acquisition returns <c>false</c> without throwing, every exception anywhere
/// in the method is caught/logged/turned into <c>false</c>.
/// </summary>
public class DaprDistributedLockServiceTests
{
    private const string LockStoreName = "lockstore";

    private static (Mock<DaprClient> DaprClient, Mock<ILogger<DaprDistributedLockService>> Logger, FakeTimeProvider TimeProvider, DaprDistributedLockService Sut)
        CreateSut()
    {
        var daprClient = new Mock<DaprClient>();
        var logger = new Mock<ILogger<DaprDistributedLockService>>();
        var timeProvider = new FakeTimeProvider();
        var sut = new DaprDistributedLockService(daprClient.Object, timeProvider, logger.Object);
        return (daprClient, logger, timeProvider, sut);
    }

    private static void VerifyLogged(Mock<ILogger<DaprDistributedLockService>> logger, LogLevel level, string containing, Times times) =>
        logger.Verify(l => l.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(containing)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

    [Fact]
    public async Task Lock_acquired_workload_succeeds_returns_true_and_releases_the_lock_in_finally()
    {
        var (daprClient, _, _, sut) = CreateSut();
        daprClient.Setup(c => c.Lock(LockStoreName, "my-lock", It.IsAny<string>(), 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TryLockResponse { Success = true });
        daprClient.Setup(c => c.Unlock(LockStoreName, "my-lock", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResponse(LockStatus.Success));
        var workloadRan = false;

        var result = await sut.ExecuteWithLockAsync("my-lock", 30, _ =>
        {
            workloadRan = true;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        workloadRan.Should().BeTrue();
        daprClient.Verify(c => c.Unlock(LockStoreName, "my-lock", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Lock_and_unlock_use_the_same_hardcoded_store_name_and_the_same_generated_owner_token()
    {
        var (daprClient, _, _, sut) = CreateSut();
        string? lockOwner = null;
        daprClient.Setup(c => c.Lock(LockStoreName, "owner-lock", It.IsAny<string>(), 30, It.IsAny<CancellationToken>()))
            .Callback<string, string, string, int, CancellationToken>((_, _, owner, _, _) => lockOwner = owner)
            .ReturnsAsync(new TryLockResponse { Success = true });
        daprClient.Setup(c => c.Unlock(LockStoreName, "owner-lock", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResponse(LockStatus.Success));

        await sut.ExecuteWithLockAsync("owner-lock", 30, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        lockOwner.Should().NotBeNullOrWhiteSpace();
        lockOwner.Should().MatchRegex($"^{System.Text.RegularExpressions.Regex.Escape(Environment.MachineName)}-[0-9a-fA-F-]{{36}}$",
            "legacy owner format is \"{MachineName}-{Guid}\"");
        daprClient.Verify(c => c.Unlock(LockStoreName, "owner-lock", lockOwner!, It.IsAny<CancellationToken>()), Times.Once,
            "the same owner token used to acquire the lock must be used to release it");
    }

    [Fact]
    public async Task Lock_not_acquired_returns_false_without_running_the_workload_or_throwing()
    {
        var (daprClient, _, _, sut) = CreateSut();
        daprClient.Setup(c => c.Lock(LockStoreName, "busy-lock", It.IsAny<string>(), 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TryLockResponse { Success = false });
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
    public async Task Lock_not_acquired_never_calls_Unlock()
    {
        var (daprClient, _, _, sut) = CreateSut();
        daprClient.Setup(c => c.Lock(LockStoreName, "busy-lock", It.IsAny<string>(), 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TryLockResponse { Success = false });

        await sut.ExecuteWithLockAsync("busy-lock", 30, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        daprClient.Verify(c => c.Unlock(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Dapr_Lock_throwing_is_caught_logged_and_returns_false()
    {
        var (daprClient, logger, _, sut) = CreateSut();
        daprClient.Setup(c => c.Lock(LockStoreName, "boom-lock", It.IsAny<string>(), 30, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("lockstore unreachable"));

        var act = async () => await sut.ExecuteWithLockAsync("boom-lock", 30, _ => Task.CompletedTask);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().BeFalse();
        VerifyLogged(logger, LogLevel.Error, "Failed to acquire or process with lock", Times.Once());
    }

    [Fact]
    public async Task Workloads_own_exception_is_caught_returns_false_and_the_lock_is_still_released()
    {
        var (daprClient, logger, _, sut) = CreateSut();
        daprClient.Setup(c => c.Lock(LockStoreName, "throwing-workload", It.IsAny<string>(), 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TryLockResponse { Success = true });
        daprClient.Setup(c => c.Unlock(LockStoreName, "throwing-workload", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResponse(LockStatus.Success));

        var act = async () => await sut.ExecuteWithLockAsync("throwing-workload", 30, _ => throw new InvalidOperationException("workload blew up"));

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().BeFalse();
        daprClient.Verify(c => c.Unlock(LockStoreName, "throwing-workload", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
            "the finally block must release the lock even when the workload throws");
        VerifyLogged(logger, LogLevel.Error, "Failed to acquire or process with lock", Times.Once());
    }

    [Fact]
    public async Task Unlock_throwing_after_a_successful_workload_is_caught_and_still_returns_false()
    {
        var (daprClient, logger, _, sut) = CreateSut();
        daprClient.Setup(c => c.Lock(LockStoreName, "unlock-boom", It.IsAny<string>(), 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TryLockResponse { Success = true });
        daprClient.Setup(c => c.Unlock(LockStoreName, "unlock-boom", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unlock failed"));

        var act = async () => await sut.ExecuteWithLockAsync("unlock-boom", 30, _ => Task.CompletedTask);

        var result = await act.Should().NotThrowAsync("ExecuteWithLockAsync itself must never throw, even when releasing the lock fails");
        result.Subject.Should().BeFalse();
        VerifyLogged(logger, LogLevel.Error, "Failed to acquire or process with lock", Times.Once());
    }

    [Fact]
    public async Task Cooperative_cutoff_cancels_the_workload_token_returns_false_and_leaves_the_ambient_token_untouched()
    {
        var (daprClient, logger, timeProvider, sut) = CreateSut();
        daprClient.Setup(c => c.Lock(LockStoreName, "slow-lock", It.IsAny<string>(), 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TryLockResponse { Success = true });
        daprClient.Setup(c => c.Unlock(LockStoreName, "slow-lock", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResponse(LockStatus.Success));
        using var ambientCts = new CancellationTokenSource();

        var result = await sut.ExecuteWithLockAsync("slow-lock", 30, workToken =>
        {
            // Simulate the workload running long enough to cross the 5/6 (25s)
            // cutoff — no real wait, the fake clock is advanced directly.
            timeProvider.Advance(TimeSpan.FromSeconds(25));
            workToken.IsCancellationRequested.Should().BeTrue();
            throw new OperationCanceledException(workToken);
        }, ambientCts.Token);

        result.Should().BeFalse();
        ambientCts.IsCancellationRequested.Should().BeFalse("the cooperative cutoff must never touch the ambient token");
        VerifyLogged(logger, LogLevel.Warning, "Operations cancelled for lock", Times.Once());
        // The cooperative-cutoff cancellation must be caught distinctly, not fall
        // through to the generic exception handler.
        VerifyLogged(logger, LogLevel.Error, "Failed to acquire or process with lock", Times.Never());
    }

    [Fact]
    public async Task Cooperative_cutoff_cancellation_still_releases_the_lock()
    {
        var (daprClient, _, timeProvider, sut) = CreateSut();
        daprClient.Setup(c => c.Lock(LockStoreName, "slow-lock-2", It.IsAny<string>(), 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TryLockResponse { Success = true });
        daprClient.Setup(c => c.Unlock(LockStoreName, "slow-lock-2", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResponse(LockStatus.Success));

        await sut.ExecuteWithLockAsync("slow-lock-2", 30, workToken =>
        {
            timeProvider.Advance(TimeSpan.FromSeconds(25));
            throw new OperationCanceledException(workToken);
        }, TestContext.Current.CancellationToken);

        daprClient.Verify(c => c.Unlock(LockStoreName, "slow-lock-2", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OperationCanceledException_from_the_ambient_token_is_not_mistaken_for_the_cooperative_cutoff()
    {
        // Distinguishes the two OperationCanceledException sources the legacy
        // "when" filter cares about: only the timer-driven work token's own
        // cancellation is logged as a warning and swallowed distinctly. An
        // OperationCanceledException tied to an already-cancelled ambient
        // token instead falls through to the generic catch (still logged,
        // still returns false, still never throws — but not misdiagnosed as
        // "operations cancelled to prevent lock expiry").
        var (daprClient, logger, _, sut) = CreateSut();
        daprClient.Setup(c => c.Lock(LockStoreName, "ambient-cancel-lock", It.IsAny<string>(), 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TryLockResponse { Success = true });
        daprClient.Setup(c => c.Unlock(LockStoreName, "ambient-cancel-lock", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResponse(LockStatus.Success));
        using var ambientCts = new CancellationTokenSource();
        ambientCts.Cancel();

        var result = await sut.ExecuteWithLockAsync("ambient-cancel-lock", 30, workToken => throw new OperationCanceledException(workToken), ambientCts.Token);

        result.Should().BeFalse();
        VerifyLogged(logger, LogLevel.Warning, "Operations cancelled for lock", Times.Never());
        VerifyLogged(logger, LogLevel.Error, "Failed to acquire or process with lock", Times.Once());
    }

    [Fact]
    public async Task Unlock_is_called_with_an_uncancellable_token_even_when_the_ambient_token_is_cancelled_mid_workload()
    {
        var (daprClient, _, _, sut) = CreateSut();
        daprClient.Setup(c => c.Lock(LockStoreName, "shutdown-lock", It.IsAny<string>(), 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TryLockResponse { Success = true });
        CancellationToken? unlockToken = null;
        daprClient.Setup(c => c.Unlock(LockStoreName, "shutdown-lock", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, _, ct) => unlockToken = ct)
            .ReturnsAsync(new UnlockResponse(LockStatus.Success));
        using var ambientCts = new CancellationTokenSource();

        await sut.ExecuteWithLockAsync("shutdown-lock", 30, _ =>
        {
            // Simulate ambient shutdown arriving mid-workload, after the lock
            // was acquired but before cleanup runs.
            ambientCts.Cancel();
            return Task.CompletedTask;
        }, ambientCts.Token);

        unlockToken.Should().NotBeNull();
        unlockToken!.Value.CanBeCanceled.Should().BeFalse(
            "cleanup must not be skipped just because the ambient token was cancelled mid-workload");
    }

    [Fact]
    public async Task Passes_the_caller_supplied_lockExpirySeconds_through_to_Dapr_unchanged()
    {
        var (daprClient, _, _, sut) = CreateSut();
        daprClient.Setup(c => c.Lock(LockStoreName, "expiry-lock", It.IsAny<string>(), 77, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TryLockResponse { Success = true });
        daprClient.Setup(c => c.Unlock(LockStoreName, "expiry-lock", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResponse(LockStatus.Success));

        var result = await sut.ExecuteWithLockAsync("expiry-lock", 77, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        daprClient.Verify(c => c.Lock(LockStoreName, "expiry-lock", It.IsAny<string>(), 77, It.IsAny<CancellationToken>()), Times.Once);
    }
}
