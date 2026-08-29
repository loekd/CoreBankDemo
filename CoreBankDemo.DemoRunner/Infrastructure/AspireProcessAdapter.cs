using System.Diagnostics;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Application.Scenarios;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>
/// Owns starting/stopping the exact known Aspire AppHost project as a tracked child
/// process tree, and detects an attachable, fingerprint-verified existing topology.
/// Never targets an arbitrary process path and never stops an unowned process (ADR-015).
/// </summary>
public sealed class AspireProcessAdapter(HttpClient httpClient, string repositoryRoot) : IProcessAdapter
{
    private static readonly IReadOnlyDictionary<string, string> AppHostProjectPaths = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [KnownTopologyProfiles.Regular] = "CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj",
        [KnownTopologyProfiles.LoadTest] = "CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj",
    };

    private readonly Dictionary<string, Process> _owned = new(StringComparer.Ordinal);

    public Task<TopologyHandle> StartOwnedAsync(string profileName, CancellationToken ct)
    {
        if (!AppHostProjectPaths.TryGetValue(profileName, out var relativePath))
        {
            throw new ArgumentOutOfRangeException(nameof(profileName), profileName, "Not a known Aspire profile.");
        }

        var projectPath = Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", $"run --project \"{projectPath}\"")
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
            EnableRaisingEvents = true,
        };

        process.Start();
        _owned[profileName] = process;

        return Task.FromResult(new TopologyHandle(profileName, IsOwned: true, process.Id, Fingerprint: $"owned:{process.Id}"));
    }

    public async Task<TopologyHandle?> TryAttachAsync(string profileName, CancellationToken ct)
    {
        var ports = profileName == KnownTopologyProfiles.LoadTest
            ? EndpointResolver.LoadTestProfilePorts
            : EndpointResolver.RegularProfilePorts;

        var fingerprints = new List<string>();
        foreach (var (resource, _) in ports)
        {
            try
            {
                var url = EndpointResolver.HealthUrlFor(resource);
                using var response = await httpClient.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    // A partially healthy graph is never attachable.
                    return null;
                }

                fingerprints.Add($"{resource}:{(int)response.StatusCode}");
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        return new TopologyHandle(profileName, IsOwned: false, ProcessId: null, Fingerprint: string.Join(",", fingerprints));
    }

    public async Task StopOwnedAsync(TopologyHandle handle, CancellationToken ct)
    {
        if (!handle.IsOwned || !_owned.TryGetValue(handle.ProfileName, out var process))
        {
            // Never stop or restart an attached (unowned) process.
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            _owned.Remove(handle.ProfileName);
            process.Dispose();
        }
    }
}
