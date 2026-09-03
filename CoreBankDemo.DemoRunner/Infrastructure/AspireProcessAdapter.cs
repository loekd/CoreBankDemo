using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;
using System.Text.Json;

namespace CoreBankDemo.DemoRunner.Infrastructure;

public sealed class AspireProcessAdapter(string repositoryRoot) : IProcessAdapter
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);
    private readonly Dictionary<TopologyProfile, string> _ownedStarts = [];

    public async Task<TopologyHandle> StartOwnedAsync(TopologyProfile profile, CancellationToken ct)
    {
        if (profile == TopologyProfile.None)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        var result = await CommandRunner.RunAsync(
            "aspire",
            [
                "start",
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
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                JournalRedaction.Apply(string.IsNullOrWhiteSpace(result.StandardError)
                    ? "Aspire start failed."
                    : result.StandardError));
        }

        var detail = JournalRedaction.Apply(result.StandardOutput);
        _ownedStarts[profile] = detail;
        return new TopologyHandle(
            profile,
            true,
            TryReadProcessId(result.StandardOutput),
            $"owned:{profile}:{detail.GetHashCode(StringComparison.Ordinal)}");
    }

    public string GetRecentOutput(TopologyHandle handle) =>
        _ownedStarts.TryGetValue(handle.Profile, out var output) ? output : string.Empty;

    public async Task StopOwnedAsync(TopologyHandle handle, CancellationToken ct)
    {
        if (!handle.IsOwned || !_ownedStarts.ContainsKey(handle.Profile))
        {
            return;
        }

        if (handle.ProcessId is { } ownedPid)
        {
            var runningPid = await FindRunningAppHostPidAsync(handle.Profile, ct);
            if (runningPid != ownedPid)
            {
                throw new InvalidOperationException(
                    $"Ownership verification failed for {handle.Profile}; expected AppHost PID {ownedPid}, found {runningPid?.ToString() ?? "none"}.");
            }
        }

        var result = await CommandRunner.RunAsync(
            "aspire",
            [
                "stop",
                "--apphost",
                ProfileRegistry.ProjectPath(repositoryRoot, handle.Profile),
                "--non-interactive",
                "--nologo",
            ],
            repositoryRoot,
            CommandTimeout,
            ct);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                JournalRedaction.Apply(string.IsNullOrWhiteSpace(result.StandardError)
                    ? "Aspire stop failed."
                    : result.StandardError));
        }

        _ownedStarts.Remove(handle.Profile);
    }

    private async Task<int?> FindRunningAppHostPidAsync(TopologyProfile profile, CancellationToken ct)
    {
        var result = await CommandRunner.RunAsync(
            "aspire",
            ["ps", "--format", "Json", "--non-interactive", "--nologo"],
            repositoryRoot,
            CommandTimeout,
            ct);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(JournalRedaction.Apply(result.StandardError));
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("appHostPath", out var path)
                    || !item.TryGetProperty("appHostPid", out var pid)
                    || !pid.TryGetInt32(out var value))
                {
                    continue;
                }

                if (string.Equals(
                        Path.GetFullPath(path.GetString() ?? string.Empty),
                        Path.GetFullPath(ProfileRegistry.ProjectPath(repositoryRoot, profile)),
                        StringComparison.Ordinal))
                {
                    return value;
                }
            }

            return null;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Could not verify owned AppHost PID from Aspire ps output.", ex);
        }
    }

    private static int? TryReadProcessId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("appHostPid", out var pid) && pid.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
