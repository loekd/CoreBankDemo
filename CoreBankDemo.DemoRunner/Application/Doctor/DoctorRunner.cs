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
    IReadOnlyList<DoctorPortRequirement> requiredPorts) : IPreflightRunner
{
    public async Task<DoctorReport> RunAsync(CancellationToken ct)
    {
        var dotnetAvailable = await environment.IsDotnetSdkAvailableAsync(ct);
        var aspireAvailable = await environment.IsAspireCliAvailableAsync(ct);
        var containerAvailable = await environment.IsContainerRuntimeAvailableAsync(ct);
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
            var reusableKnownInfrastructure = requirement.ResourceName == KnownResources.Jaeger
                && probeStatus == HealthStatus.Healthy;
            if (!reusableKnownInfrastructure)
            {
                portAvailability[requirement.Profile] = false;
            }
            checks.Add(probeStatus == HealthStatus.Healthy
                ? DoctorCheckResult.Ok($"Port {requirement.Port} ({requirement.Profile}/{requirement.ResourceName})", "occupied by a healthy known endpoint — Attach may be available after fingerprint verification")
                : DoctorCheckResult.Fail($"Port {requirement.Port} ({requirement.Profile}/{requirement.ResourceName})", "occupied by an unknown or unhealthy process"));
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
                    ? portsFree ? "not running — Start available" : "not running, but one or more required ports are occupied"
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
}
