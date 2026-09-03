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
}

public sealed record DoctorPortRequirement(TopologyProfile Profile, string ResourceName, int Port);

public sealed class DoctorRunner(
    IEnvironmentProbe environment,
    IHealthMonitor health,
    IAspireAdapter aspire)
{
    public async Task<DoctorReport> RunAsync(
        IReadOnlyList<DoctorPortRequirement> requiredPorts,
        CancellationToken ct)
    {
        var checks = new List<DoctorCheckResult>
        {
            await environment.IsDotnetSdkAvailableAsync(ct)
                ? DoctorCheckResult.Ok(".NET SDK available")
                : DoctorCheckResult.Fail(".NET SDK available", "Install the .NET 10 SDK and ensure 'dotnet' is on PATH."),
            await environment.IsAspireCliAvailableAsync(ct)
                ? DoctorCheckResult.Ok("Aspire CLI available")
                : DoctorCheckResult.Fail("Aspire CLI available", "Install the Aspire CLI and ensure 'aspire' is on PATH."),
            await environment.IsContainerRuntimeAvailableAsync(ct)
                ? DoctorCheckResult.Ok("Container runtime available")
                : DoctorCheckResult.Fail("Container runtime available", "Start Docker or the configured container runtime."),
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
            checks.Add(probeStatus == HealthStatus.Healthy
                ? DoctorCheckResult.Ok($"Port {requirement.Port} ({requirement.Profile}/{requirement.ResourceName})", "occupied by a healthy known endpoint — Attach may be available after fingerprint verification")
                : DoctorCheckResult.Fail($"Port {requirement.Port} ({requirement.Profile}/{requirement.ResourceName})", "occupied by an unknown or unhealthy process"));
        }

        var discovered = await aspire.DiscoverAsync(ct);
        foreach (var profile in KnownTopologyProfiles.All)
        {
            var snapshot = discovered.FirstOrDefault(item => item.Profile == profile);
            checks.Add(snapshot switch
            {
                null => DoctorCheckResult.Ok($"{profile} topology", "not running — Start available"),
                { IsReady: true } => DoctorCheckResult.Ok($"{profile} topology", "running, healthy, fingerprint verified — Attach available"),
                _ => DoctorCheckResult.Fail(
                    $"{profile} topology",
                    snapshot.ErrorSummary ?? "A partial or unhealthy graph is running; inspect Aspire before attaching."),
            });
        }

        return new DoctorReport(checks);
    }
}
