using System.Diagnostics;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>Opens a known local URL using the OS default browser via shell execute.</summary>
public sealed class BrowserLauncher : IBrowserLauncher
{
    public Task<bool> OpenAsync(string linkId, CancellationToken ct)
    {
        try
        {
            var url = EndpointResolver.LinkFor(linkId);
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return Task.FromResult(process is not null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(false);
        }
    }
}
