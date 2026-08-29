using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Application.Scenarios;

namespace CoreBankDemo.DemoRunner.Application.Doctor;

public sealed record DoctorCheckResult(string Name, bool Passed, string Remediation)
{
    public static DoctorCheckResult Ok(string name, string note = "") => new(name, true, note);
    public static DoctorCheckResult Fail(string name, string remediation) => new(name, false, remediation);
}

public sealed record DoctorReport(IReadOnlyList<DoctorCheckResult> Checks, TalkScenarioDefinition? Scenario)
{
    public bool AllPassed => Checks.All(c => c.Passed);
}

/// <summary>
/// Runs the pre-show prerequisite report. Never starts an AppHost process or sends a
/// business request — only reads local prerequisite state, per the doctor I/O-matrix row.
/// </summary>
public sealed class DoctorRunner(IEnvironmentProbe environment, IHealthMonitor health)
{
    public async Task<DoctorReport> RunAsync(string scenarioPath, IReadOnlyDictionary<string, int> requiredPorts, CancellationToken ct)
    {
        var checks = new List<DoctorCheckResult>();

        var loadResult = ScenarioLoader.LoadFromFile(scenarioPath);
        checks.Add(loadResult.IsValid
            ? DoctorCheckResult.Ok("Scenario valid")
            : DoctorCheckResult.Fail("Scenario valid", string.Join(" | ", loadResult.Errors)));

        checks.Add(await environment.IsDotnetSdkAvailableAsync(ct)
            ? DoctorCheckResult.Ok(".NET SDK available")
            : DoctorCheckResult.Fail(".NET SDK available", "Install the .NET 10 SDK and ensure 'dotnet' is on PATH."));

        checks.Add(await environment.IsAspireCliAvailableAsync(ct)
            ? DoctorCheckResult.Ok("Aspire CLI available")
            : DoctorCheckResult.Fail("Aspire CLI available", "Install the Aspire CLI ('dotnet tool install -g aspire.cli' or see aspire docs)."));

        checks.Add(await environment.IsContainerRuntimeAvailableAsync(ct)
            ? DoctorCheckResult.Ok("Container runtime available")
            : DoctorCheckResult.Fail("Container runtime available", "Start Docker (or your configured container runtime) before the talk."));

        foreach (var (resourceName, port) in requiredPorts)
        {
            var isFree = await environment.IsPortFreeAsync(port, ct);
            if (isFree)
            {
                checks.Add(DoctorCheckResult.Ok($"Port {port} ({resourceName})", "free — ready to Start"));
                continue;
            }

            var probeStatus = await health.CheckAsync(resourceName, ct);
            checks.Add(probeStatus == HealthStatus.Healthy
                ? DoctorCheckResult.Ok($"Port {port} ({resourceName})", "occupied by a healthy matching resource — Attach available")
                : DoctorCheckResult.Fail($"Port {port} ({resourceName})", $"Port {port} is occupied by an unrecognized or unhealthy process. Free it before starting."));
        }

        return new DoctorReport(checks, loadResult.Scenario);
    }
}
