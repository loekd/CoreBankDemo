using System.Diagnostics;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>Opens a known Aspire or Jaeger URL using the OS default browser.</summary>
public sealed class BrowserLauncher : IBrowserLauncher
{
    public Task<LinkOpenResult> OpenAsync(string linkId, string? verifiedUrl, CancellationToken ct)
    {
        if (linkId == KnownLinks.AspireDashboard && !IsVerifiedLoopbackUrl(verifiedUrl))
        {
            return Task.FromResult(new LinkOpenResult(false, null));
        }

        var url = linkId == KnownLinks.AspireDashboard
            ? verifiedUrl!
            : EndpointResolver.LinkFor(linkId);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return Task.FromResult(new LinkOpenResult(process is not null, url));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No default browser reachable (e.g. a headless sandbox with no
            // xdg-open) -- the URL is still valid and worth returning so the
            // caller can offer it for terminal-side viewing/copying instead.
            return Task.FromResult(new LinkOpenResult(false, url));
        }
    }

    private static bool IsVerifiedLoopbackUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.IsLoopback
        && uri.Scheme is "http" or "https";
}
