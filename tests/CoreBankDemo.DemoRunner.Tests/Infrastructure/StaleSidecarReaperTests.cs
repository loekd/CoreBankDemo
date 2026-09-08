using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Infrastructure;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

/// <summary>
/// A sidecar orphaned by an earlier run sits in this console's Redis consumer group and is
/// handed a share of every delivery that nothing then reads, so the next run reports Listening
/// while a portion of its payments never resolve. These tests pin both halves of the contract:
/// the orphan is found, and nothing else ever is.
/// </summary>
public class StaleSidecarReaperTests
{
    private const string AppId = "demorunner-console";

    /// <summary>Real `ps -axww -o pid=,command=` output, including the orphan that prompted this.</summary>
    private const string PsOutput = """
          812 /usr/libexec/secinitd
         1715616 /home/agent/.dapr/bin/daprd --app-id demorunner-console --resources-path /repo/dapr/components --dapr-grpc-port 53100 --dapr-http-port 53101
         1720044 /home/agent/.dapr/bin/daprd --app-id payments-api --resources-path /repo/dapr/components --dapr-grpc-port 50001
         1720051 /home/agent/.dapr/bin/daprd --app-id corebank-api --resources-path /repo/dapr/components --dapr-grpc-port 50011
         1730000 dotnet run --project CoreBankDemo.DemoRunner
        """;

    [Fact]
    public void Parse_FindsTheConsolesOwnOrphan()
    {
        StaleSidecarReaper.Parse(PsOutput, AppId, currentProcessId: 1730000)
            .Should().Equal(1715616);
    }

    /// <summary>
    /// The one rule that must never bend. Killing an AppHost sidecar would take the bank down
    /// mid-demo, which is far worse than the stale feed this class exists to fix.
    /// </summary>
    [Theory]
    [InlineData("payments-api")]
    [InlineData("corebank-api")]
    public void Parse_NeverMatchesABankingSidecar(string bankingAppId)
    {
        var pids = StaleSidecarReaper.Parse(PsOutput, AppId, currentProcessId: 1730000);

        var bankingLine = PsOutput
            .Split('\n')
            .Single(line => line.Contains($"--app-id {bankingAppId}", StringComparison.Ordinal));
        var bankingPid = int.Parse(bankingLine.Trim().Split(' ')[0]);

        pids.Should().NotContain(bankingPid);
    }

    /// <summary>
    /// A prefix match would be a live hazard the day someone adds a `demorunner-console-2`:
    /// matching is on the flag together with its value, so a longer app-id is a different one.
    /// </summary>
    [Fact]
    public void Parse_DoesNotMatchADifferentAppIdThatSharesAPrefix()
    {
        const string output = "  4242 daprd --app-id demorunner-console-loadtest --dapr-grpc-port 53100";

        StaleSidecarReaper.Parse(output, AppId, currentProcessId: 1).Should().BeEmpty();
    }

    /// <summary>
    /// Anything may quote the app-id on its own command line -- a `ps | grep`, a `pkill -f`, an
    /// editor, a shell running any of them. Only a process that actually is daprd may be killed.
    /// </summary>
    [Theory]
    [InlineData("  900 grep --color=auto -- --app-id demorunner-console")]
    [InlineData("  901 pkill -f --app-id demorunner-console")]
    [InlineData("  902 /bin/bash -c ps -axww | grep -- '--app-id demorunner-console'")]
    [InlineData("  903 /usr/bin/dotnet run -- --app-id demorunner-console")]
    public void Parse_NeverMatchesAProcessThatMerelyQuotesTheAppId(string line)
    {
        StaleSidecarReaper.Parse(line, AppId, currentProcessId: 1).Should().BeEmpty();
    }

    [Fact]
    public void Parse_NeverMatchesTheRunningConsoleItself()
    {
        const string output = "  777 dotnet CoreBankDemo.DemoRunner.dll --app-id demorunner-console";

        StaleSidecarReaper.Parse(output, AppId, currentProcessId: 777).Should().BeEmpty();
    }

    [Fact]
    public void Parse_OnAMachineWithNoPreviousRun_FindsNothing()
    {
        StaleSidecarReaper.Parse(PsOutput, "demorunner-console-absent", currentProcessId: 1)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task ReapAsync_TerminatesEveryOrphanItFinds()
    {
        var terminator = new RecordingTerminator();
        var reaper = new StaleSidecarReaper(new StubCommandRunner(CommandOutput.Success(PsOutput)), terminator);

        var reaped = await reaper.ReapAsync(AppId, CancellationToken.None);

        reaped.Should().Equal(1715616);
        terminator.Terminated.Should().Equal(1715616);
    }

    /// <summary>
    /// A machine whose process list cannot be read still gets a working feed; only the orphan
    /// cleanup is unavailable, and that must not be turned into a failure to start.
    /// </summary>
    [Fact]
    public async Task ReapAsync_WhenTheProcessListCannotBeRead_ReapsNothingAndDoesNotThrow()
    {
        var terminator = new RecordingTerminator();
        var reaper = new StaleSidecarReaper(new StubCommandRunner(CommandOutput.Missing("ps not found")), terminator);

        (await reaper.ReapAsync(AppId, CancellationToken.None)).Should().BeEmpty();
        terminator.Terminated.Should().BeEmpty();
    }

    /// <summary>
    /// A process that vanished between the listing and the kill is the normal race, not an
    /// error: it is already in the state the reaper wanted.
    /// </summary>
    [Fact]
    public async Task ReapAsync_WhenAnOrphanCannotBeSignalled_KeepsGoing()
    {
        var terminator = new ThrowingTerminator();
        var reaper = new StaleSidecarReaper(new StubCommandRunner(CommandOutput.Success(PsOutput)), terminator);

        (await reaper.ReapAsync(AppId, CancellationToken.None)).Should().BeEmpty();
    }

    private sealed class StubCommandRunner(CommandOutput output) : ICommandRunner
    {
        public Task<CommandOutput> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken ct,
            IReadOnlyDictionary<string, string>? environment = null) => Task.FromResult(output);
    }

    private sealed class RecordingTerminator : IOwnedProcessTerminator
    {
        public List<int> Terminated { get; } = [];

        public Task EnsureExitedAsync(int processId, TimeSpan gracefulWait, CancellationToken ct)
        {
            Terminated.Add(processId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingTerminator : IOwnedProcessTerminator
    {
        public Task EnsureExitedAsync(int processId, TimeSpan gracefulWait, CancellationToken ct) =>
            throw new InvalidOperationException("already gone");
    }
}
