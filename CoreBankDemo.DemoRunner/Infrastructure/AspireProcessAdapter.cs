using System.Text.Json;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

public sealed class AspireProcessAdapter : IProcessAdapter
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);
    private readonly string _repositoryRoot;
    private readonly ICommandRunner _commands;
    private readonly IOwnedProcessTerminator _terminator;
    private readonly Dictionary<TopologyProfile, TopologyHandle> _owned = [];
    private readonly Dictionary<TopologyProfile, string> _output = [];

    public AspireProcessAdapter(string repositoryRoot)
        : this(repositoryRoot, new CommandRunner(), new OwnedProcessTerminator())
    {
    }

    public AspireProcessAdapter(string repositoryRoot, ICommandRunner commands)
        : this(repositoryRoot, commands, new OwnedProcessTerminator())
    {
    }

    public AspireProcessAdapter(
        string repositoryRoot,
        ICommandRunner commands,
        IOwnedProcessTerminator terminator)
    {
        _repositoryRoot = repositoryRoot;
        _commands = commands;
        _terminator = terminator;
    }

    public async Task<TopologyHandle> StartOwnedAsync(TopologyProfile profile, CancellationToken ct)
    {
        if (profile == TopologyProfile.None)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        var before = await GetRunningAppHostsAsync(ct);
        var projectPath = Path.GetFullPath(ProfileRegistry.ProjectPath(_repositoryRoot, profile));
        if (before.Any(item => SamePath(item.Path, projectPath)))
        {
            throw new InvalidOperationException($"{profile} is already running and cannot be claimed as owned.");
        }

        var result = await _commands.RunAsync(
            "aspire",
            [
                "start",
                "--apphost",
                projectPath,
                "--format",
                "Json",
                "--non-interactive",
                "--nologo",
            ],
            _repositoryRoot,
            CommandTimeout,
            ct);
        _output[profile] = JournalRedaction.Apply(
            string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError }.Where(value => !string.IsNullOrWhiteSpace(value))));

        var outputPid = TryReadProcessId(result.StandardOutput);
        if (result.TimedOut)
        {
            await CleanupNewAppHostAsync(profile, projectPath, before, outputPid, trustOutputPid: true, CancellationToken.None);
            throw new TimeoutException($"aspire start timed out for {profile}; any newly started AppHost was stopped.");
        }

        if (!result.Succeeded)
        {
            await CleanupNewAppHostAsync(profile, projectPath, before, outputPid, trustOutputPid: true, CancellationToken.None);
            throw new InvalidOperationException(
                JournalRedaction.Apply(string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"aspire start exited with code {result.ExitCode}."
                    : result.StandardError));
        }

        if (outputPid is null)
        {
            await CleanupNewAppHostAsync(profile, projectPath, before, null, trustOutputPid: false, CancellationToken.None);
            throw new InvalidOperationException("aspire start succeeded without a valid AppHost PID; ownership was rejected.");
        }

        var after = await GetRunningAppHostsAsync(ct);
        var exact = after.Where(item => SamePath(item.Path, projectPath)).ToList();
        if (exact.Count != 1 || exact[0].ProcessId != outputPid.Value)
        {
            await CleanupNewAppHostAsync(profile, projectPath, before, outputPid, trustOutputPid: false, CancellationToken.None);
            throw new InvalidOperationException(
                $"aspire start PID verification failed for {profile}; output PID {outputPid.Value}, discovered {string.Join(", ", exact.Select(item => item.ProcessId))}.");
        }

        var handle = new TopologyHandle(
            profile,
            true,
            outputPid.Value,
            $"owned:{profile}:{outputPid.Value}",
            projectPath);
        _owned[profile] = handle;
        return handle;
    }

    public string GetRecentOutput(TopologyHandle handle) =>
        _output.TryGetValue(handle.Profile, out var output) ? output : string.Empty;

    public async Task<OwnedStopResult> StopOwnedAsync(TopologyHandle handle, CancellationToken ct)
    {
        if (!handle.IsOwned || handle.ProcessId is null)
        {
            throw new InvalidOperationException("Stop requires a verified owned handle with an exact AppHost PID.");
        }

        if (!_owned.TryGetValue(handle.Profile, out var stored)
            || stored.ProcessId != handle.ProcessId
            || !string.Equals(stored.Fingerprint, handle.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied AppHost handle is not owned by this process adapter session.");
        }

        var projectPath = Path.GetFullPath(ProfileRegistry.ProjectPath(_repositoryRoot, handle.Profile));
        var running = await GetRunningAppHostsAsync(ct);
        var exact = running.Where(item => SamePath(item.Path, projectPath)).ToList();
        if (exact.Count != 1 || exact[0].ProcessId != handle.ProcessId.Value)
        {
            throw new InvalidOperationException(
                $"Ownership verification failed for {handle.Profile}; expected PID {handle.ProcessId.Value}, found {string.Join(", ", exact.Select(item => item.ProcessId))}.");
        }

        var result = await RunStopAsync(projectPath, ct);
        var forced = result.TimedOut || !result.Succeeded;
        await _terminator.EnsureExitedAsync(handle.ProcessId.Value, TimeSpan.FromSeconds(15), ct);
        var after = await GetRunningAppHostsAsync(ct);
        if (after.Any(item => SamePath(item.Path, projectPath) && item.ProcessId == handle.ProcessId.Value))
        {
            throw new InvalidOperationException($"Owned AppHost PID {handle.ProcessId.Value} is still running after aspire stop.");
        }

        _owned.Remove(handle.Profile);
        _output.Remove(handle.Profile);
        var detail = forced
            ? $"Aspire stop did not complete cleanly; exact owned PID {handle.ProcessId.Value} was terminated."
            : $"Aspire stopped exact owned PID {handle.ProcessId.Value} gracefully.";
        return new OwnedStopResult(forced, detail);
    }

    public async Task ForgetExitedOwnedAsync(TopologyHandle handle, CancellationToken ct)
    {
        if (!handle.IsOwned || handle.ProcessId is null
            || !_owned.TryGetValue(handle.Profile, out var stored)
            || stored.ProcessId != handle.ProcessId)
        {
            throw new InvalidOperationException("Cannot forget an AppHost that is not owned by this adapter session.");
        }

        var projectPath = Path.GetFullPath(ProfileRegistry.ProjectPath(_repositoryRoot, handle.Profile));
        var running = await GetRunningAppHostsAsync(ct);
        if (running.Any(item => SamePath(item.Path, projectPath) || item.ProcessId == handle.ProcessId.Value))
        {
            throw new InvalidOperationException("Cannot forget ownership while the AppHost is still running.");
        }

        _owned.Remove(handle.Profile);
        _output.Remove(handle.Profile);
    }

    private async Task<IReadOnlyList<RunningAppHost>> GetRunningAppHostsAsync(CancellationToken ct)
    {
        var result = await _commands.RunAsync(
            "aspire",
            ["ps", "--format", "Json", "--non-interactive", "--nologo"],
            _repositoryRoot,
            CommandTimeout,
            ct);
        if (result.TimedOut)
        {
            throw new TimeoutException("aspire ps timed out while verifying AppHost ownership.");
        }

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                JournalRedaction.Apply(string.IsNullOrWhiteSpace(result.StandardError)
                    ? "aspire ps failed while verifying AppHost ownership."
                    : result.StandardError));
        }

        return AspireProcessJsonParser.Parse(result.StandardOutput);
    }

    private async Task CleanupNewAppHostAsync(
        TopologyProfile profile,
        string projectPath,
        IReadOnlyList<RunningAppHost> before,
        int? outputPid,
        bool trustOutputPid,
        CancellationToken ct)
    {
        var hadExisting = before.Any(item => SamePath(item.Path, projectPath));
        if (trustOutputPid && outputPid is not null && !hadExisting)
        {
            await RunStopAsync(projectPath, ct);
            await _terminator.EnsureExitedAsync(outputPid.Value, TimeSpan.FromSeconds(5), ct);
            _owned.Remove(profile);
            return;
        }

        IReadOnlyList<RunningAppHost> after;
        try
        {
            after = await GetRunningAppHostsAsync(ct);
        }
        catch
        {
            return;
        }

        var beforePids = before
            .Where(item => SamePath(item.Path, projectPath))
            .Select(item => item.ProcessId)
            .ToHashSet();
        var candidates = after
            .Where(item => SamePath(item.Path, projectPath)
                && !beforePids.Contains(item.ProcessId)
                && (outputPid is null || item.ProcessId == outputPid.Value))
            .ToList();
        if (candidates.Count != 1)
        {
            return;
        }

        await RunStopAsync(projectPath, ct);
        await _terminator.EnsureExitedAsync(candidates[0].ProcessId, TimeSpan.FromSeconds(5), ct);
        _owned.Remove(profile);
    }

    private Task<CommandOutput> RunStopAsync(string projectPath, CancellationToken ct) =>
        _commands.RunAsync(
            "aspire",
            ["stop", "--apphost", projectPath, "--non-interactive", "--nologo"],
            _repositoryRoot,
            CommandTimeout,
            ct);

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.Ordinal);

    private static int? TryReadProcessId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("appHostPid", out var pid)
                && pid.TryGetInt32(out var value)
                && value > 0
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
