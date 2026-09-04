namespace CoreBankDemo.DemoRunner.Application.Ports;

/// <summary>Opens an allow-listed local dashboard URL in the OS default browser.</summary>
public interface IBrowserLauncher
{
    Task<bool> OpenAsync(string linkId, string? verifiedUrl, CancellationToken ct);
}
