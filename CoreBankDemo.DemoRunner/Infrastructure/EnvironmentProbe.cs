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

    public Task<bool> IsDevProxyAvailableAsync(CancellationToken ct) => RunProbeCommandAsync("devproxy", "--version", ct);

    public Task<bool> IsPortFreeAsync(int port, CancellationToken ct) =>
        Task.FromResult(IsLoopbackBindable(IPAddress.Loopback, port) && IsLoopbackBindable(IPAddress.IPv6Loopback, port));

    /// <summary>
    /// Reports whether the port can be bound on one loopback family. Both families are
    /// probed because a listener on <c>::1</c> alone still blocks a "localhost" endpoint
    /// while leaving the IPv4 bind free. Only "address in use" (and a denied privileged
    /// port) count as occupied — every other socket error, such as an address family this
    /// machine does not configure, would otherwise be reported as a phantom busy port.
    /// </summary>
    private static bool IsLoopbackBindable(IPAddress address, int port)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
        {
            return true;
        }

        try
        {
            using var listener = new TcpListener(address, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            return false;
        }
        catch (SocketException)
        {
            return true;
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
