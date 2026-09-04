using System.Diagnostics;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>Opens a known Aspire or Jaeger URL using the OS default browser.</summary>
public sealed class BrowserLauncher : IBrowserLauncher
{
    public Task<bool> OpenAsync(string linkId, string? verifiedUrl, CancellationToken ct)
    {
        try
        {
            if (linkId == KnownLinks.AspireDashboard && !IsVerifiedLoopbackUrl(verifiedUrl))
            {
                return Task.FromResult(false);
            }

            var url = linkId == KnownLinks.AspireDashboard
                ? verifiedUrl!
                : EndpointResolver.LinkFor(linkId);
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return Task.FromResult(process is not null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(false);
        }
    }

    private static bool IsVerifiedLoopbackUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.IsLoopback
        && uri.Scheme is "http" or "https";
}
