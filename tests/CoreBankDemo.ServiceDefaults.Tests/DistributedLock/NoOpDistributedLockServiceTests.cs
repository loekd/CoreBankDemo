using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.DistributedLock;

/// <summary>
/// Story 3.2: <see cref="NoOpDistributedLockService"/> always reports "lock not
/// acquired" and never runs the workload — used for lock-free hosting (LoadTestSupport).
/// The type is <c>internal</c>; reachable here via the project's
/// <c>InternalsVisibleTo</c> to <c>CoreBankDemo.ServiceDefaults.Tests</c>.
/// </summary>
public class NoOpDistributedLockServiceTests
{
    [Fact]
    public async Task ExecuteWithLockAsync_returns_false_without_running_the_workload()
    {
        var sut = new NoOpDistributedLockService();
        var workloadRan = false;

        var result = await sut.ExecuteWithLockAsync(
            "any-lock",
            30,
            _ =>
            {
                workloadRan = true;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        result.Should().BeFalse();
        workloadRan.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteWithLockAsync_never_calls_Dapr_and_never_throws_even_with_a_cancelled_ambient_token()
    {
        var sut = new NoOpDistributedLockService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sut.ExecuteWithLockAsync(
            "any-lock",
            30,
            _ => Task.CompletedTask,
            cts.Token);

        var result = await act.Should().NotThrowAsync("NoOp never touches Dapr or the workload, so an already-cancelled ambient token is irrelevant");
        result.Subject.Should().BeFalse();
    }
}
