namespace CoreBankDemo.DemoRunner.Application.Ports;

/// <summary>Opens a known local URL (<see cref="Scenarios.KnownLinks"/>) in the OS default browser.</summary>
public interface IBrowserLauncher
{
    Task<bool> OpenAsync(string linkId, CancellationToken ct);
}
