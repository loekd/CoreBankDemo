using AwesomeAssertions;
using System.Diagnostics;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Infrastructure;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

public class AspireAdapterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Discovery_CliFailureOrTimeout_IsUnreachable(bool missing)
    {
        var commands = new RecordingCommandRunner();
        commands.Queue(missing ? CommandOutput.Missing("not found") : CommandOutput.Timeout());
        var adapter = new AspireCliAdapter("/repo", TimeProvider.System, commands);

        var result = await adapter.DiscoverAsync(CancellationToken.None);

        result.IsReachable.Should().BeFalse();
        result.Snapshots.Should().BeEmpty();
        result.ErrorSummary.Should().NotBeNullOrWhiteSpace();
        commands.Calls.Single().Arguments.Should().Equal("ps", "--format", "Json", "--non-interactive", "--nologo");
    }

    [Fact]
    public async Task Discovery_EmptyProcessList_IsReachableNoTopology()
    {
        var commands = new RecordingCommandRunner();
        commands.Queue(CommandOutput.Success("[]"));
        var adapter = new AspireCliAdapter("/repo", TimeProvider.System, commands);

        var result = await adapter.DiscoverAsync(CancellationToken.None);

        result.IsReachable.Should().BeTrue();
        result.Snapshots.Should().BeEmpty();
    }

    [Fact]
    public async Task Discovery_MalformedProcessJson_IsUnreachable()
    {
        var commands = new RecordingCommandRunner();
        commands.Queue(CommandOutput.Success("{}"));

        var result = await new AspireCliAdapter("/repo", TimeProvider.System, commands)
            .DiscoverAsync(CancellationToken.None);

        result.IsReachable.Should().BeFalse();
        result.ErrorSummary.Should().Contain("root is not an array");
    }

    [Fact]
    public async Task Discovery_RunningAppHostDescribeTimeout_IsUnreachable()
    {
        var project = "/repo/CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj";
        var commands = new RecordingCommandRunner();
        commands.Queue(
            CommandOutput.Success(Ps(project, 42)),
            CommandOutput.Timeout());

        var result = await new AspireCliAdapter("/repo", TimeProvider.System, commands)
            .DiscoverAsync(CancellationToken.None);

        result.IsReachable.Should().BeFalse();
        result.ErrorSummary.Should().Contain("describe timed out");
    }

    [Fact]
    public async Task Describe_UsesExactSupportedArgvAndParsesEndpointFingerprint()
    {
        var commands = new RecordingCommandRunner();
        commands.Queue(CommandOutput.Success(ValidRegularDescribeJson()));
        var adapter = new AspireCliAdapter("/repo", TimeProvider.System, commands);

        var snapshot = await adapter.GetSnapshotAsync(TopologyProfile.Regular, CancellationToken.None);

        snapshot.IsReady.Should().BeTrue();
        commands.Calls.Single().Arguments.Should().Equal(
            "describe",
            "--apphost",
            "/repo/CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj",
            "--format",
            "Json",
            "--non-interactive",
            "--nologo");
    }

    [Theory]
    [MemberData(nameof(DescribeFailures))]
    public async Task Describe_Failures_AreUnreachable(CommandOutput output, string expected)
    {
        var commands = new RecordingCommandRunner();
        commands.Queue(output);

        var snapshot = await new AspireCliAdapter("/repo", TimeProvider.System, commands)
            .GetSnapshotAsync(TopologyProfile.Regular, CancellationToken.None);

        snapshot.IsReachable.Should().BeFalse();
        snapshot.ErrorSummary.Should().Contain(expected);
    }

    public static TheoryData<CommandOutput, string> DescribeFailures => new()
    {
        { CommandOutput.Missing("missing"), "unavailable" },
        { CommandOutput.Timeout(), "timed out" },
        { CommandOutput.Failure(2, "bad exit"), "bad exit" },
        { CommandOutput.Success(""), "no JSON" },
    };

    [Fact]
    public async Task ResourceCommand_UsesConcreteReplicaArgv()
    {
        var commands = new RecordingCommandRunner();
        commands.Queue(
            CommandOutput.Success(ValidRegularDescribeJson()),
            CommandOutput.Success("one"),
            CommandOutput.Success("two"));
        var adapter = new AspireCliAdapter("/repo", TimeProvider.System, commands);

        var result = await adapter.ExecuteResourceCommandAsync(
            TopologyProfile.Regular,
            KnownResources.CoreBankApi,
            ResourceCommand.Restart,
            CancellationToken.None);

        result.Status.Should().Be(ResourceDispatchStatus.Dispatched);
        result.AffectedInstances.Should().Equal("corebank-api-a", "corebank-api-b");
        commands.Calls[1].Arguments.Should().Equal(
            "resource", "corebank-api-a", "restart",
            "--apphost", "/repo/CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj",
            "--non-interactive", "--nologo");
        commands.Calls[2].Arguments.Should().Equal(
            "resource", "corebank-api-b", "restart",
            "--apphost", "/repo/CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj",
            "--non-interactive", "--nologo");
    }

    [Fact]
    public async Task ResourceCommand_TimeoutAfterDispatch_IsAmbiguous()
    {
        var commands = new RecordingCommandRunner();
        commands.Queue(
            CommandOutput.Success(ValidRegularDescribeJson()),
            CommandOutput.Timeout("partial"));
        var adapter = new AspireCliAdapter("/repo", TimeProvider.System, commands);

        var result = await adapter.ExecuteResourceCommandAsync(
            TopologyProfile.Regular,
            KnownResources.CoreBankApi,
            ResourceCommand.Restart,
            CancellationToken.None);

        result.Status.Should().Be(ResourceDispatchStatus.Ambiguous);
        result.RequiresRefresh.Should().BeTrue();
        result.FailedInstances.Should().ContainSingle().Which.Should().Be("corebank-api-a");
    }

    [Fact]
    public async Task ResourceCommand_SecondReplicaFailure_IsPartial()
    {
        var commands = new RecordingCommandRunner();
        commands.Queue(
            CommandOutput.Success(ValidRegularDescribeJson()),
            CommandOutput.Success("first restarted"),
            CommandOutput.Failure(1, "second failed"));
        var adapter = new AspireCliAdapter("/repo", TimeProvider.System, commands);

        var result = await adapter.ExecuteResourceCommandAsync(
            TopologyProfile.Regular,
            KnownResources.CoreBankApi,
            ResourceCommand.Restart,
            CancellationToken.None);

        result.Status.Should().Be(ResourceDispatchStatus.Partial);
        result.AffectedInstances.Should().ContainSingle().Which.Should().Be("corebank-api-a");
        result.FailedInstances.Should().ContainSingle().Which.Should().Be("corebank-api-b");
    }

    [Fact]
    public async Task ProcessAdapter_StartAndStop_VerifiesExactPidAndArgv()
    {
        var project = "/repo/CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj";
        var pid = DeadPid();
        var commands = new RecordingCommandRunner();
        commands.Queue(
            CommandOutput.Success("[]"),
            CommandOutput.Success($$"""{"appHostPid":{{pid}}}"""),
            CommandOutput.Success(Ps(project, pid)),
            CommandOutput.Success(Ps(project, pid)),
            CommandOutput.Success(),
            CommandOutput.Success("[]"));
        var adapter = new AspireProcessAdapter("/repo", commands);

        var handle = await adapter.StartOwnedAsync(TopologyProfile.Regular, armFaults: true, CancellationToken.None);
        await adapter.StopOwnedAsync(handle, CancellationToken.None);

        handle.ProcessId.Should().Be(pid);
        handle.ProjectPath.Should().Be(project);
        commands.Calls[1].Arguments.Should().Equal(
            "start", "--apphost", project, "--format", "Json", "--non-interactive", "--nologo");
        commands.Calls[4].Arguments.Should().Equal(
            "stop", "--apphost", project, "--non-interactive", "--nologo");
        commands.Calls[1].Environment.Should().NotBeNull()
            .And.Contain(new KeyValuePair<string, string>("Features__UseDevProxy", "true"));
        commands.Calls[0].Environment.Should().BeNull("only the start call carries the arming decision");
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task ProcessAdapter_Start_PassesTheArmingDecisionAsAnEnvironmentVariable(
        bool armFaults,
        string expected)
    {
        var project = "/repo/CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj";
        var pid = DeadPid();
        var commands = new RecordingCommandRunner();
        commands.Queue(
            CommandOutput.Success("[]"),
            CommandOutput.Success($"{{\"appHostPid\":{pid}}}"),
            CommandOutput.Success(Ps(project, pid)));
        var adapter = new AspireProcessAdapter("/repo", commands);

        await adapter.StartOwnedAsync(TopologyProfile.Regular, armFaults, CancellationToken.None);

        // Env beats appsettings.json, so the console's arming decision is explicit for both
        // profiles rather than inherited from whichever default each AppHost ships.
        commands.Calls[1].Environment.Should().Contain(
            new KeyValuePair<string, string>("Features__UseDevProxy", expected));
    }

    [Fact]
    public async Task ProcessAdapter_MissingPid_RejectsOwnershipAndCleansNewProcess()
    {
        var project = "/repo/CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj";
        var commands = new RecordingCommandRunner();
        commands.Queue(
            CommandOutput.Success("[]"),
            CommandOutput.Success("{}"),
            CommandOutput.Success(Ps(project, DeadPid())),
            CommandOutput.Success());
        var adapter = new AspireProcessAdapter("/repo", commands);

        var act = () => adapter.StartOwnedAsync(TopologyProfile.Regular, armFaults: true, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*without a valid AppHost PID*");
        commands.Calls.Should().Contain(call => call.Arguments.First() == "stop");
    }

    [Fact]
    public async Task ProcessAdapter_StartTimeout_CleansNewExactPid()
    {
        var project = "/repo/CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj";
        var pid = DeadPid();
        var commands = new RecordingCommandRunner();
        commands.Queue(
            CommandOutput.Success("[]"),
            CommandOutput.Timeout($$"""{"appHostPid":{{pid}}}"""),
            CommandOutput.Success(Ps(project, pid)),
            CommandOutput.Success());
        var adapter = new AspireProcessAdapter("/repo", commands);

        var act = () => adapter.StartOwnedAsync(TopologyProfile.Regular, armFaults: true, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        commands.Calls.Should().Contain(call => call.Arguments.First() == "stop");
    }

    [Fact]
    public async Task ProcessAdapter_PidMismatch_RejectsOwnership()
    {
        var project = "/repo/CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj";
        var commands = new RecordingCommandRunner();
        commands.Queue(
            CommandOutput.Success("[]"),
            CommandOutput.Success("""{"appHostPid":42}"""),
            CommandOutput.Success(Ps(project, 43)),
            CommandOutput.Success(Ps(project, 43)));
        var adapter = new AspireProcessAdapter("/repo", commands);

        var act = () => adapter.StartOwnedAsync(TopologyProfile.Regular, armFaults: true, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*PID verification failed*");
        commands.Calls.Should().NotContain(call => call.Arguments.First() == "stop");
    }

    [Fact]
    public async Task ProcessAdapter_StopPidMismatch_NeverRunsStop()
    {
        var project = "/repo/CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj";
        var commands = new RecordingCommandRunner();
        commands.Queue(
            CommandOutput.Success("[]"),
            CommandOutput.Success("""{"appHostPid":42}"""),
            CommandOutput.Success(Ps(project, 42)),
            CommandOutput.Success(Ps(project, 43)));
        var adapter = new AspireProcessAdapter("/repo", commands);
        var handle = await adapter.StartOwnedAsync(TopologyProfile.Regular, armFaults: true, CancellationToken.None);

        var act = () => adapter.StopOwnedAsync(handle, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ownership verification failed*");
        commands.Calls.Should().NotContain(call => call.Arguments.First() == "stop");
    }

    [Fact]
    public async Task ProcessAdapter_StopTimeoutOrFailure_ForceTerminatesExactOwnedPid()
    {
        var project = "/repo/CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj";
        var pid = DeadPid();
        foreach (var stopResult in new[] { CommandOutput.Timeout(), CommandOutput.Failure(2, "stop failed") })
        {
            var commands = new RecordingCommandRunner();
            commands.Queue(
                CommandOutput.Success("[]"),
                CommandOutput.Success($$"""{"appHostPid":{{pid}}}"""),
                CommandOutput.Success(Ps(project, pid)),
                CommandOutput.Success(Ps(project, pid)),
                stopResult,
                CommandOutput.Success("[]"));
            var adapter = new AspireProcessAdapter("/repo", commands);
            var handle = await adapter.StartOwnedAsync(TopologyProfile.Regular, armFaults: true, CancellationToken.None);

            var result = await adapter.StopOwnedAsync(handle, CancellationToken.None);

            result.Forced.Should().BeTrue();
            adapter.GetRecentOutput(handle).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ProcessAdapter_ForgetExitedOwnership_RequiresConfirmedAbsence()
    {
        var project = "/repo/CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj";
        var commands = new RecordingCommandRunner();
        commands.Queue(
            CommandOutput.Success("[]"),
            CommandOutput.Success("""{"appHostPid":42}"""),
            CommandOutput.Success(Ps(project, 42)),
            CommandOutput.Success("[]"));
        var adapter = new AspireProcessAdapter("/repo", commands);
        var handle = await adapter.StartOwnedAsync(TopologyProfile.Regular, armFaults: true, CancellationToken.None);

        await adapter.ForgetExitedOwnedAsync(handle, CancellationToken.None);

        adapter.GetRecentOutput(handle).Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessAdapter_StopWithUnownedOrMissingPidHandle_Throws()
    {
        var adapter = new AspireProcessAdapter("/repo", new RecordingCommandRunner());

        var unowned = () => adapter.StopOwnedAsync(
            new TopologyHandle(TopologyProfile.Regular, false, 42, "attached", "/repo/app.csproj"),
            CancellationToken.None);
        var missingPid = () => adapter.StopOwnedAsync(
            new TopologyHandle(TopologyProfile.Regular, true, null, "owned", "/repo/app.csproj"),
            CancellationToken.None);

        await unowned.Should().ThrowAsync<InvalidOperationException>();
        await missingPid.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CommandRunner_MissingExecutable_ReturnsStartFailure()
    {
        var result = await new CommandRunner().RunAsync(
            $"missing-{Guid.NewGuid():N}",
            [],
            Directory.GetCurrentDirectory(),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        result.ProcessStarted.Should().BeFalse();
        result.StartFailed.Should().BeTrue();
    }

    [Fact]
    public async Task CommandRunner_Timeout_KillsExactProcessAndReturnsTimedOut()
    {
        var result = await new CommandRunner().RunAsync(
            "/usr/bin/tail",
            ["-f", "/dev/null"],
            Directory.GetCurrentDirectory(),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        result.ProcessStarted.Should().BeTrue();
        result.TimedOut.Should().BeTrue();
    }

    [Fact]
    public async Task CommandRunner_SuccessAndFailure_PreserveExitAndOutput()
    {
        var runner = new CommandRunner();

        var success = await runner.RunAsync(
            "dotnet", ["--version"], Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(10), CancellationToken.None);
        var failure = await runner.RunAsync(
            "dotnet", ["definitely-not-a-command"], Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(10), CancellationToken.None);

        success.Succeeded.Should().BeTrue();
        success.StandardOutput.Should().NotBeNullOrWhiteSpace();
        failure.Succeeded.Should().BeFalse();
        failure.ExitCode.Should().NotBe(0);
    }

    [Fact]
    public async Task CommandRunner_CallerCancellation_KillsProcessAndPropagatesCancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var act = () => new CommandRunner().RunAsync(
            "/usr/bin/tail",
            ["-f", "/dev/null"],
            Directory.GetCurrentDirectory(),
            TimeSpan.FromSeconds(10),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task OwnedProcessTerminator_ForceStopsExactProcessAfterGracePeriod()
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/tail")
        {
            UseShellExecute = false,
            ArgumentList = { "-f", "/dev/null" },
        })!;

        await new OwnedProcessTerminator().EnsureExitedAsync(
            process.Id,
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        process.HasExited.Should().BeTrue();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""[{"appHostPath":"/repo/app.csproj"}]""")]
    [InlineData("""[{"appHostPath":"/repo/app.csproj","appHostPid":0}]""")]
    public void ProcessJson_MissingOrMalformedPid_IsRejected(string json)
    {
        var act = () => AspireProcessJsonParser.Parse(json);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// A PID guaranteed not to belong to any running process. A hardcoded literal
    /// (e.g. 42) works on a dev machine but on a fresh GitHub Actions runner may
    /// coincide with a real system process; OwnedProcessTerminator then makes a
    /// genuine Process.GetProcessById/Kill call against it and fails with
    /// "Operation not permitted" instead of the intended no-op.
    /// </summary>
    private static int DeadPid()
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/true") { UseShellExecute = false })!;
        process.WaitForExit();
        return process.Id;
    }

    private static string Ps(string project, int pid) =>
        $$"""[{"appHostPath":"{{project}}","appHostPid":{{pid}}}]""";

    private static string ValidRegularDescribeJson() =>
        """
        {
          "resources": [
            {"name":"postgres-x","displayName":"postgres","resourceType":"Container","state":"Running","healthStatus":"Healthy"},
            {"name":"redis-x","displayName":"redis","resourceType":"Container","state":"Running","healthStatus":"Healthy"},
            {"name":"jaeger-x","displayName":"jaeger","resourceType":"Container","state":"Running","healthStatus":"Healthy","urls":[{"url":"http://localhost:16686"}]},
            {"name":"corebank-api-a","displayName":"corebank-api","resourceType":"Project","state":"Running","healthStatus":"Healthy","urls":[{"url":"http://127.0.0.1:5032/swagger"}],"commands":{"restart":{"state":"Enabled"},"stop":{"state":"Enabled"}}},
            {"name":"corebank-api-b","displayName":"corebank-api","resourceType":"Project","state":"Running","healthStatus":"Healthy","urls":[{"url":"http://127.0.0.1:5032/swagger"}],"commands":{"restart":{"state":"Enabled"},"stop":{"state":"Enabled"}}},
            {"name":"payments-api-a","displayName":"payments-api","resourceType":"Project","state":"Running","healthStatus":"Healthy","urls":[{"url":"http://127.0.0.1:5294/swagger"}]},
            {"name":"payments-api-b","displayName":"payments-api","resourceType":"Project","state":"Running","healthStatus":"Healthy","urls":[{"url":"http://127.0.0.1:5294/swagger"}]}
          ]
        }
        """;
}

internal sealed class RecordingCommandRunner : ICommandRunner
{
    private readonly Queue<CommandOutput> _results = new();
    public List<CommandCall> Calls { get; } = [];

    public void Queue(params CommandOutput[] results)
    {
        foreach (var result in results)
        {
            _results.Enqueue(result);
        }
    }

    public Task<CommandOutput> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        Calls.Add(new CommandCall(fileName, arguments.ToList(), workingDirectory, timeout, environment));
        return Task.FromResult(_results.Dequeue());
    }
}

internal sealed record CommandCall(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string>? Environment = null);
