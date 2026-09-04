using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Application.Ports;

public enum HealthStatus
{
    Unknown,
    Healthy,
    Unhealthy,
    Unreachable,
}

/// <summary>Probes the health of a known resource (<see cref="Scenarios.KnownResources"/>).</summary>
public interface IHealthMonitor
{
    Task<HealthStatus> CheckAsync(string resourceName, CancellationToken ct);

    Task<HealthStatus> CheckAsync(string resourceName, TopologyProfile profile, CancellationToken ct) =>
        CheckAsync(resourceName, ct);

    /// <summary>Polls <see cref="CheckAsync"/> until healthy or the timeout elapses.</summary>
    Task<bool> WaitForHealthyAsync(string resourceName, TimeSpan timeout, CancellationToken ct);
}
