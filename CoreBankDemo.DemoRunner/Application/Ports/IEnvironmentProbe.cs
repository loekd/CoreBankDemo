namespace CoreBankDemo.DemoRunner.Application.Ports;

/// <summary>Probes local prerequisites (SDK, Aspire CLI, container runtime, ports) for the doctor report.</summary>
public interface IEnvironmentProbe
{
    Task<bool> IsDotnetSdkAvailableAsync(CancellationToken ct);
    Task<bool> IsAspireCliAvailableAsync(CancellationToken ct);
    Task<bool> IsContainerRuntimeAvailableAsync(CancellationToken ct);
    Task<bool> IsPortFreeAsync(int port, CancellationToken ct);
}
