using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

public sealed class AspireCliAdapter(string repositoryRoot, TimeProvider time) : IAspireAdapter
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<TopologySnapshot>> DiscoverAsync(CancellationToken ct)
    {
        CommandOutput processes;
        try
        {
            processes = await CommandRunner.RunAsync(
                "aspire",
                ["ps", "--format", "Json", "--non-interactive", "--nologo"],
                repositoryRoot,
                CommandTimeout,
                ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
        if (!processes.Succeeded || string.Equals(processes.StandardOutput.Trim(), "[]", StringComparison.Ordinal))
        {
            return [];
        }

        var snapshots = new List<TopologySnapshot>();
        foreach (var profile in KnownTopologyProfiles.All)
        {
            var snapshot = await GetSnapshotAsync(profile, ct);
            if (snapshot.IsReachable)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    public async Task<TopologySnapshot> GetSnapshotAsync(TopologyProfile profile, CancellationToken ct)
    {
        if (profile == TopologyProfile.None)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        CommandOutput result;
        try
        {
            result = await CommandRunner.RunAsync(
                "aspire",
                [
                    "describe",
                    "--apphost",
                    ProfileRegistry.ProjectPath(repositoryRoot, profile),
                    "--format",
                    "Json",
                    "--non-interactive",
                    "--nologo",
                ],
                repositoryRoot,
                CommandTimeout,
                ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return TopologySnapshot.Unreachable(profile, time.GetUtcNow(), ex.Message);
        }

        if (!result.Succeeded)
        {
            return TopologySnapshot.Unreachable(
                profile,
                time.GetUtcNow(),
                JournalRedaction.Apply(string.IsNullOrWhiteSpace(result.StandardError)
                    ? "Aspire describe failed."
                    : result.StandardError));
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return TopologySnapshot.Unreachable(profile, time.GetUtcNow(), "No running AppHost was found for this profile.");
        }

        return AspireJsonParser.Parse(profile, result.StandardOutput, time.GetUtcNow());
    }

    public async Task<ResourceCommandResult> ExecuteResourceCommandAsync(
        TopologyProfile profile,
        string resourceName,
        ResourceCommand command,
        CancellationToken ct)
    {
        if (!KnownResources.ResourceCommandAllowList.Contains(resourceName))
        {
            return new ResourceCommandResult(false, $"Resource '{resourceName}' is not allow-listed.");
        }

        var snapshot = await GetSnapshotAsync(profile, ct);
        var resource = snapshot.FindResource(resourceName);
        if (!snapshot.IsReachable || !snapshot.IsFingerprintMatch || resource is null)
        {
            return new ResourceCommandResult(false, snapshot.ErrorSummary ?? $"Resource '{resourceName}' is not present in the verified graph.");
        }

        if (!resource.Supports(command))
        {
            return new ResourceCommandResult(false, $"Aspire does not expose '{command}' for resource '{resourceName}' in the fresh snapshot.");
        }

        var instances = resource.InstanceNames is { Count: > 0 }
            ? resource.InstanceNames
            : [resourceName];
        var details = new List<string>();
        try
        {
            foreach (var instanceName in instances)
            {
                var result = await CommandRunner.RunAsync(
                    "aspire",
                    [
                        "resource",
                        instanceName,
                        command.ToString().ToLowerInvariant(),
                        "--apphost",
                        ProfileRegistry.ProjectPath(repositoryRoot, profile),
                        "--non-interactive",
                        "--nologo",
                    ],
                    repositoryRoot,
                    CommandTimeout,
                    ct);
                var detail = string.Join(
                    Environment.NewLine,
                    new[] { result.StandardOutput, result.StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
                details.Add($"{instanceName}: {detail}");
                if (!result.Succeeded)
                {
                    return new ResourceCommandResult(false, JournalRedaction.Apply(string.Join(Environment.NewLine, details)));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return new ResourceCommandResult(false, ex.Message);
        }

        return new ResourceCommandResult(true, JournalRedaction.Apply(string.Join(Environment.NewLine, details)));
    }
}
