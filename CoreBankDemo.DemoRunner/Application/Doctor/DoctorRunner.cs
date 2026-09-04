using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Application.Doctor;

public sealed record DoctorCheckResult(string Name, bool Passed, string Remediation)
{
    public static DoctorCheckResult Ok(string name, string note = "") => new(name, true, note);
    public static DoctorCheckResult Fail(string name, string remediation) => new(name, false, remediation);
}

public sealed record DoctorReport(IReadOnlyList<DoctorCheckResult> Checks)
{
    public bool AllPassed => Checks.All(check => check.Passed);

    public required bool EnvironmentReady { get; init; }
    public required bool DiscoveryReachable { get; init; }
    public required IReadOnlyDictionary<TopologyProfile, ProfilePreflightResult> Profiles { get; init; }

    public bool CanStart(TopologyProfile profile) =>
        EnvironmentReady
        && DiscoveryReachable
        && Profiles.TryGetValue(profile, out var result)
        && result.CanStart;
}

public sealed record DoctorPortRequirement(TopologyProfile Profile, string ResourceName, int Port);

public sealed record ProfilePreflightResult(
    TopologyProfile Profile,
    bool PortsFree,
    TopologySnapshot? Snapshot,
    bool CanStart,
    bool CanAttach,
    string Detail);

public interface IPreflightRunner
{
    Task<DoctorReport> RunAsync(CancellationToken ct);
}

public sealed class DoctorRunner(
    IEnvironmentProbe environment,
    IHealthMonitor health,
    IAspireAdapter aspire,
    IReadOnlyList<DoctorPortRequirement> requiredPorts,
    TimeProvider? time = null) : IPreflightRunner
{
    /// <summary>
    /// How long the three CLI probes are reused. The console re-runs preflight on every
    /// poll while no topology is active; shelling out to 'dotnet', 'aspire' and
    /// 'docker info' that often is slow enough that a probe can hit its own timeout and
    /// report a phantom preflight failure.
    /// </summary>
    private static readonly TimeSpan EnvironmentProbeTtl = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _environmentGate = new(1, 1);
    private EnvironmentAvailability? _environmentCache;
    private DateTimeOffset _environmentCapturedAt;

    public async Task<DoctorReport> RunAsync(CancellationToken ct)
    {
        var (dotnetAvailable, aspireAvailable, containerAvailable) = await GetEnvironmentAsync(ct);
        var checks = new List<DoctorCheckResult>
        {
            dotnetAvailable
                ? DoctorCheckResult.Ok(".NET SDK available")
                : DoctorCheckResult.Fail(".NET SDK available", "Install the .NET 10 SDK and ensure 'dotnet' is on PATH."),
            aspireAvailable
                ? DoctorCheckResult.Ok("Aspire CLI available")
                : DoctorCheckResult.Fail("Aspire CLI available", "Install the Aspire CLI and ensure 'aspire' is on PATH."),
            containerAvailable
                ? DoctorCheckResult.Ok("Container runtime available")
                : DoctorCheckResult.Fail("Container runtime available", "Start Docker or the configured container runtime."),
        };

        var portAvailability = new Dictionary<TopologyProfile, bool>
        {
            [TopologyProfile.Regular] = true,
            [TopologyProfile.LoadTests] = true,
        };
        var knownEndpointPorts = new Dictionary<TopologyProfile, List<int>>
        {
            [TopologyProfile.Regular] = [],
            [TopologyProfile.LoadTests] = [],
        };
        var strangerPorts = new Dictionary<TopologyProfile, List<int>>
        {
            [TopologyProfile.Regular] = [],
            [TopologyProfile.LoadTests] = [],
        };
        foreach (var requirement in requiredPorts)
        {
            if (await environment.IsPortFreeAsync(requirement.Port, ct))
            {
                checks.Add(DoctorCheckResult.Ok(
                    $"Port {requirement.Port} ({requirement.Profile}/{requirement.ResourceName})",
                    "free — Start available"));
                continue;
            }

            var probeStatus = await health.CheckAsync(requirement.ResourceName, requirement.Profile, ct);
            var persistent = KnownResources.PersistentInfrastructure.Contains(requirement.ResourceName);
            var reusableKnownInfrastructure = persistent && probeStatus == HealthStatus.Healthy;
            if (!reusableKnownInfrastructure)
            {
                portAvailability[requirement.Profile] = false;
                (probeStatus == HealthStatus.Healthy
                    ? knownEndpointPorts
                    : strangerPorts)[requirement.Profile].Add(requirement.Port);
            }

            checks.Add((probeStatus, persistent) switch
            {
                (HealthStatus.Healthy, true) => DoctorCheckResult.Ok(
                    $"Port {requirement.Port} ({requirement.Profile}/{requirement.ResourceName})",
                    $"held by the persistent {requirement.ResourceName} container — Aspire reuses it, Start stays available"),
                (HealthStatus.Healthy, false) => DoctorCheckResult.Ok(
                    $"Port {requirement.Port} ({requirement.Profile}/{requirement.ResourceName})",
                    "occupied by a healthy known endpoint — Attach may be available after fingerprint verification"),
                (_, true) => DoctorCheckResult.Fail(
                    $"Port {requirement.Port} ({requirement.Profile}/{requirement.ResourceName})",
                    PersistentContainerRemediation(requirement.Port, requirement.ResourceName, probeStatus)),
                _ => DoctorCheckResult.Fail(
                    $"Port {requirement.Port} ({requirement.Profile}/{requirement.ResourceName})",
                    PortHolderRemediation(requirement.Port, requirement.ResourceName, probeStatus)),
            });
        }

        var discovered = await aspire.DiscoverAsync(ct);
        if (!discovered.IsReachable)
        {
            checks.Add(DoctorCheckResult.Fail(
                "Aspire discovery",
                discovered.ErrorSummary ?? "Aspire discovery is unreachable."));
        }

        var profiles = new Dictionary<TopologyProfile, ProfilePreflightResult>();
        var readyActiveProfile = discovered.Snapshots.FirstOrDefault(snapshot => snapshot.IsReady)?.Profile;
        foreach (var profile in KnownTopologyProfiles.All)
        {
            var snapshot = discovered.Snapshots.FirstOrDefault(item => item.Profile == profile);
            var portsFree = portAvailability[profile];
            var canStart = discovered.IsReachable && snapshot is null && portsFree;
            var canAttach = discovered.IsReachable && snapshot?.IsReady == true;
            var detail = !discovered.IsReachable
                ? discovered.ErrorSummary ?? "Aspire discovery failed."
                : snapshot is null
                    ? NotRunningDetail(knownEndpointPorts[profile], strangerPorts[profile])
                    : canAttach
                        ? "running, healthy, endpoint fingerprint verified — Attach available"
                        : snapshot.ErrorSummary ?? "partial, stale, or unhealthy graph";
            profiles[profile] = new ProfilePreflightResult(profile, portsFree, snapshot, canStart, canAttach, detail);
            checks.Add(snapshot switch
            {
                null when discovered.IsReachable && (portsFree || readyActiveProfile is not null) =>
                    DoctorCheckResult.Ok(
                        $"{profile} topology",
                        portsFree ? detail : $"unavailable while {readyActiveProfile} is active"),
                { IsReady: true } when discovered.IsReachable => DoctorCheckResult.Ok($"{profile} topology", detail),
                _ => DoctorCheckResult.Fail(
                    $"{profile} topology",
                    detail),
            });
        }

        return new DoctorReport(checks)
        {
            EnvironmentReady = dotnetAvailable && aspireAvailable && containerAvailable,
            DiscoveryReachable = discovered.IsReachable,
            Profiles = profiles,
        };
    }

    private async Task<EnvironmentAvailability> GetEnvironmentAsync(CancellationToken ct)
    {
        await _environmentGate.WaitAsync(ct);
        try
        {
            if (_environmentCache is { } cached && _time.GetUtcNow() - _environmentCapturedAt <= EnvironmentProbeTtl)
            {
                return cached;
            }

            var fresh = new EnvironmentAvailability(
                await environment.IsDotnetSdkAvailableAsync(ct),
                await environment.IsAspireCliAvailableAsync(ct),
                await environment.IsContainerRuntimeAvailableAsync(ct));
            _environmentCache = fresh;
            _environmentCapturedAt = _time.GetUtcNow();
            return fresh;
        }
        finally
        {
            _environmentGate.Release();
        }
    }

    private sealed record EnvironmentAvailability(bool Dotnet, bool Aspire, bool Container);

    /// <summary>
    /// Describes a profile that Aspire does not report as running. Ports answering a known
    /// health endpoint mean the stack is up but was launched outside the Aspire CLI, which
    /// is a different problem — and a different remedy — from a stranger holding the port.
    /// </summary>
    private static string NotRunningDetail(IReadOnlyList<int> knownEndpointPorts, IReadOnlyList<int> strangerPorts)
    {
        if (knownEndpointPorts.Count == 0 && strangerPorts.Count == 0)
        {
            return "not running — Start available";
        }

        var parts = new List<string>();
        if (knownEndpointPorts.Count > 0)
        {
            parts.Add(
                $"known endpoints already answer on {string.Join(", ", knownEndpointPorts)} while 'aspire ps' lists no AppHost "
                + "for this profile — the stack was started outside the Aspire CLI, or another profile holds a shared port. "
                + "Attach cannot verify it; relaunch it with 'aspire run' (or stop it) to drive it from this console");
        }

        if (strangerPorts.Count > 0)
        {
            parts.Add($"required ports held by unknown or unhealthy processes: {string.Join(", ", strangerPorts)}");
        }

        return string.Join(" | ", parts);
    }

    /// <summary>
    /// A persistent container outlives its AppHost, so an occupied port is expected here.
    /// The failure means its port answers nothing recognisable, which usually means a
    /// stale container from another project rather than this repository's own.
    /// </summary>
    private static string PersistentContainerRemediation(int port, string resourceName, HealthStatus probeStatus) =>
        $"TCP port {port} is held but the persistent {resourceName} container did not answer (probe: {probeStatus}). "
        + $"Check it with 'docker ps --filter publish={port}' — a stale {resourceName} from another project keeps this port "
        + $"across AppHost restarts; remove it, or wait for the running one to finish starting.";

    /// <summary>
    /// Names the exact port and the tools that can find its holder. A plain "ps" does not
    /// reveal container-published ports or a listener held by an already-exited parent, so
    /// the remediation has to point at the tools that do.
    /// </summary>
    private static string PortHolderRemediation(int port, string resourceName, HealthStatus probeStatus) =>
        $"TCP port {port} is held by something that is not a healthy {resourceName} (probe: {probeStatus}). "
        + $"Find the holder with 'lsof -nP -iTCP:{port} -sTCP:LISTEN' or 'docker ps --filter publish={port}' — "
        + "'ps' alone does not show container-published ports.";
}
