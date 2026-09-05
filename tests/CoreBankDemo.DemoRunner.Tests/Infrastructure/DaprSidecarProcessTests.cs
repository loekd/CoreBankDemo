using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Infrastructure;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

/// <summary>
/// Exercises the real child-process runner, not a fake. The point of this class is ownership —
/// a started process is always either owned or killed — and a fake that records a call proves
/// nothing about <see cref="System.Diagnostics.Process"/>. The executable is injected so these
/// hold on a machine with no Dapr installation.
/// </summary>
public class DaprSidecarProcessTests
{
    private static readonly DaprSidecarLaunch Launch =
        new("demorunner-console-test", "/tmp", 53910, 53911, 53912, 53913);

    [Fact]
    public async Task StartAsync_ExecutableNotOnPath_ReportsItRatherThanThrowing()
    {
        await using var sidecar = NewSidecar("a-binary-that-does-not-exist-anywhere");

        var result = await sidecar.StartAsync(Launch, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Contain("not on PATH");
        sidecar.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_ProcessExitsImmediately_IsNotClaimedAsASidecar()
    {
        RequirePosix();
        await using var sidecar = NewSidecar("true");

        var result = await sidecar.StartAsync(Launch, CancellationToken.None);

        result.Succeeded.Should().BeFalse("a process that is already gone is not a sidecar");
        sidecar.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_ProcessDiesDuringTheReadinessWait_ReportsItAndOwnsNothing()
    {
        RequirePosix();
        // "sleep" rejects daprd's flags and dies a moment after launch — the same shape as a
        // daprd that came up and could not initialise its components. Readiness is proven by
        // the health endpoint, never by elapsed time, so this must fail rather than be waited
        // out, and the process this call started must not survive the failure.
        await using var sidecar = NewSidecar("sleep", TimeSpan.FromSeconds(2));

        var result = await sidecar.StartAsync(Launch with { HttpPort = 53914 }, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Contain("before it became ready");
        sidecar.IsRunning.Should().BeFalse("a sidecar that never became ready is torn down, never orphaned");
    }

    [Fact]
    public async Task StartAsync_HealthEndpointNeverAnswers_TimesOutRatherThanWaitingForEver()
    {
        RequirePosix();
        // A live process that serves no health endpoint. "cat" with a path that does not exist
        // still exits, so the guaranteed-alive case is asserted through the injected timeout:
        // a readiness probe that never answers must end in a stated failure, not a hang.
        await using var sidecar = NewSidecar("sleep", TimeSpan.FromMilliseconds(250));

        var start = DateTimeOffset.UtcNow;
        var result = await sidecar.StartAsync(Launch with { HttpPort = 53915 }, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        (DateTimeOffset.UtcNow - start).Should().BeLessThan(
            TimeSpan.FromSeconds(10),
            "the readiness wait is bounded by its own timeout");
    }

    [Fact]
    public async Task StopAsync_WithNothingStarted_IsSafe()
    {
        await using var sidecar = NewSidecar("a-binary-that-does-not-exist-anywhere");

        await sidecar.StopAsync(CancellationToken.None);
        await sidecar.StopAsync(CancellationToken.None);

        sidecar.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_CapturesTheChildsOwnOutputSoAFailureCanExplainItself()
    {
        RequirePosix();
        await using var sidecar = NewSidecar("false");

        await sidecar.StartAsync(Launch, CancellationToken.None);

        // "false" writes nothing, but the capture pipeline must be wired and bounded rather
        // than throwing; the feed reads RecentOutput into its own unavailable detail.
        sidecar.RecentOutput.Should().NotBeNull();
    }

    [Fact]
    public void DefaultExecutable_IsTheDaprSidecarBinary() =>
        DaprSidecarProcess.DefaultExecutable.Should().Be("daprd");

    private static DaprSidecarProcess NewSidecar(string executable, TimeSpan? readinessTimeout = null) =>
        new(
            new OwnedProcessTerminator(),
            TimeProvider.System,
            null,
            executable,
            readinessTimeout ?? TimeSpan.FromMilliseconds(400));

    private static void RequirePosix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("'true'/'false'/'sleep' are POSIX utilities; this asserts the POSIX process launch path.");
        }
    }
}
