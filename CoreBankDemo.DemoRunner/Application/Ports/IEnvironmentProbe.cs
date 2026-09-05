namespace CoreBankDemo.DemoRunner.Application.Ports;

/// <summary>Probes local prerequisites (SDK, Aspire CLI, container runtime, ports) for the doctor report.</summary>
public interface IEnvironmentProbe
{
    Task<bool> IsDotnetSdkAvailableAsync(CancellationToken ct);
    Task<bool> IsAspireCliAvailableAsync(CancellationToken ct);
    Task<bool> IsContainerRuntimeAvailableAsync(CancellationToken ct);

    /// <summary>
    /// Dev Proxy is opt-in, so this is only a hard prerequisite when the operator asks for
    /// fault arming. Probed so a missing binary is reported before a start rather than as an
    /// opaque AppHost failure.
    /// </summary>
    Task<bool> IsDevProxyAvailableAsync(CancellationToken ct);
    Task<bool> IsPortFreeAsync(int port, CancellationToken ct);
}
