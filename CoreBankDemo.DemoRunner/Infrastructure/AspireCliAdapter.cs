using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

public sealed class AspireCliAdapter : IAspireAdapter
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private readonly string _repositoryRoot;
    private readonly TimeProvider _time;
    private readonly ICommandRunner _commands;

    public AspireCliAdapter(string repositoryRoot, TimeProvider time)
        : this(repositoryRoot, time, new CommandRunner())
    {
    }

    public AspireCliAdapter(string repositoryRoot, TimeProvider time, ICommandRunner commands)
    {
        _repositoryRoot = repositoryRoot;
        _time = time;
        _commands = commands;
    }

    public async Task<TopologyDiscoveryResult> DiscoverAsync(CancellationToken ct)
    {
        var processes = await _commands.RunAsync(
            "aspire",
            ["ps", "--format", "Json", "--non-interactive", "--nologo"],
            _repositoryRoot,
            CommandTimeout,
            ct);
        if (!processes.ProcessStarted || processes.StartFailed)
        {
            return TopologyDiscoveryResult.Unreachable($"Aspire CLI is unavailable: {processes.StandardError}");
        }

        if (processes.TimedOut)
        {
            return TopologyDiscoveryResult.Unreachable("aspire ps timed out.");
        }

        if (!processes.Succeeded)
        {
            return TopologyDiscoveryResult.Unreachable(
                JournalRedaction.Apply(string.IsNullOrWhiteSpace(processes.StandardError)
                    ? $"aspire ps exited with code {processes.ExitCode}."
                    : processes.StandardError));
        }

        IReadOnlyList<string> runningPaths;
        try
        {
            runningPaths = AspireProcessJsonParser.ParsePaths(processes.StandardOutput);
        }
        catch (InvalidOperationException ex)
        {
            return TopologyDiscoveryResult.Unreachable(ex.Message);
        }

        if (runningPaths.Count == 0)
        {
            return TopologyDiscoveryResult.Success([]);
        }

        var snapshots = new List<TopologySnapshot>();
        foreach (var profile in KnownTopologyProfiles.All)
        {
            var profilePath = Path.GetFullPath(ProfileRegistry.ProjectPath(_repositoryRoot, profile));
            if (!runningPaths.Any(path => string.Equals(Path.GetFullPath(path), profilePath, StringComparison.Ordinal)))
            {
                continue;
            }

            var snapshot = await GetSnapshotAsync(profile, ct);
            if (!snapshot.IsReachable)
            {
                return TopologyDiscoveryResult.Unreachable(
                    snapshot.ErrorSummary ?? $"Could not describe running {profile} AppHost.");
            }

            snapshots.Add(snapshot);
        }

        return TopologyDiscoveryResult.Success(snapshots);
    }

    public async Task<TopologySnapshot> GetSnapshotAsync(TopologyProfile profile, CancellationToken ct)
    {
        if (profile == TopologyProfile.None)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        var result = await _commands.RunAsync(
            "aspire",
            [
                "describe",
                "--apphost",
                ProfileRegistry.ProjectPath(_repositoryRoot, profile),
                "--format",
                "Json",
                "--non-interactive",
                "--nologo",
            ],
            _repositoryRoot,
            CommandTimeout,
            ct);

        if (!result.ProcessStarted || result.StartFailed)
        {
            return TopologySnapshot.Unreachable(profile, _time.GetUtcNow(), $"Aspire CLI is unavailable: {result.StandardError}");
        }

        if (result.TimedOut)
        {
            return TopologySnapshot.Unreachable(profile, _time.GetUtcNow(), "aspire describe timed out.");
        }

        if (!result.Succeeded)
        {
            return TopologySnapshot.Unreachable(
                profile,
                _time.GetUtcNow(),
                JournalRedaction.Apply(string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"aspire describe exited with code {result.ExitCode}."
                    : result.StandardError));
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return TopologySnapshot.Unreachable(profile, _time.GetUtcNow(), "aspire describe returned no JSON.");
        }

        return AspireJsonParser.Parse(profile, result.StandardOutput, _time.GetUtcNow());
    }

    public async Task<ResourceCommandResult> ExecuteResourceCommandAsync(
        TopologyProfile profile,
        string resourceName,
        ResourceCommand command,
        CancellationToken ct)
    {
        if (!KnownResources.ResourceCommandAllowList.Contains(resourceName))
        {
            return ResourceCommandResult.Rejected($"Resource '{resourceName}' is not allow-listed.");
        }

        var snapshot = await GetSnapshotAsync(profile, ct);
        var resource = snapshot.FindResource(resourceName);
        if (!snapshot.IsReachable || !snapshot.IsFingerprintMatch || resource is null)
        {
            return ResourceCommandResult.Rejected(snapshot.ErrorSummary ?? $"Resource '{resourceName}' is not present in the verified graph.");
        }

        if (!resource.Supports(command))
        {
            return ResourceCommandResult.Rejected($"Aspire does not expose '{command}' for resource '{resourceName}' in the fresh snapshot.");
        }

        var instances = resource.InstanceNames is { Count: > 0 }
            ? resource.InstanceNames
            : [resourceName];
        var affected = new List<string>();
        var failed = new List<string>();
        var details = new List<string>();

        foreach (var instanceName in instances)
        {
            var result = await _commands.RunAsync(
                "aspire",
                [
                    "resource",
                    instanceName,
                    command.ToString().ToLowerInvariant(),
                    "--apphost",
                    ProfileRegistry.ProjectPath(_repositoryRoot, profile),
                    "--non-interactive",
                    "--nologo",
                ],
                _repositoryRoot,
                CommandTimeout,
                ct);
            var detail = string.Join(
                Environment.NewLine,
                new[] { result.StandardOutput, result.StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
            details.Add($"{instanceName}: {detail}");

            if (result.TimedOut)
            {
                return new ResourceCommandResult(
                    ResourceDispatchStatus.Ambiguous,
                    JournalRedaction.Apply($"Timed out after dispatching {command} to {instanceName}. Refresh is required.{Environment.NewLine}{string.Join(Environment.NewLine, details)}"),
                    affected,
                    [instanceName]);
            }

            if (!result.Succeeded)
            {
                failed.Add(instanceName);
                return new ResourceCommandResult(
                    affected.Count > 0 ? ResourceDispatchStatus.Partial : ResourceDispatchStatus.Rejected,
                    JournalRedaction.Apply(string.Join(Environment.NewLine, details)),
                    affected,
                    failed);
            }

            affected.Add(instanceName);
        }

        return new ResourceCommandResult(
            ResourceDispatchStatus.Dispatched,
            JournalRedaction.Apply(string.Join(Environment.NewLine, details)),
            affected,
            failed);
    }
}
