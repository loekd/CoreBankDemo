using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>Probes local prerequisites by shelling out to the documented CLIs and checking TCP ports.</summary>
public sealed class EnvironmentProbe : IEnvironmentProbe
{
    public Task<bool> IsDotnetSdkAvailableAsync(CancellationToken ct) => RunProbeCommandAsync("dotnet", "--version", ct);

    public Task<bool> IsAspireCliAvailableAsync(CancellationToken ct) => RunProbeCommandAsync("aspire", "--version", ct);

    public Task<bool> IsContainerRuntimeAvailableAsync(CancellationToken ct) => RunProbeCommandAsync("docker", "info", ct);

    public Task<bool> IsPortFreeAsync(int port, CancellationToken ct)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return Task.FromResult(true);
        }
        catch (SocketException)
        {
            return Task.FromResult(false);
        }
    }

    private static async Task<bool> RunProbeCommandAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };

            process.Start();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }
}
