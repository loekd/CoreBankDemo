using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Infrastructure;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

public class EnvironmentProbeTests
{
    [Fact]
    public async Task IsPortFreeAsync_UnboundPort_ReportsFree()
    {
        var probe = new EnvironmentProbe();

        var free = await probe.IsPortFreeAsync(FreePort(), TestContext.Current.CancellationToken);

        free.Should().BeTrue();
    }

    [Fact]
    public async Task IsPortFreeAsync_PortHeldOnIPv4Loopback_ReportsOccupied()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var free = await new EnvironmentProbe().IsPortFreeAsync(port, TestContext.Current.CancellationToken);

            free.Should().BeFalse();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task IsPortFreeAsync_PortHeldOnIPv6LoopbackOnly_ReportsOccupied()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }

        var port = FreePort();
        var listener = new TcpListener(IPAddress.IPv6Loopback, port);
        listener.Server.DualMode = false;
        listener.Start();
        try
        {
            var free = await new EnvironmentProbe().IsPortFreeAsync(port, TestContext.Current.CancellationToken);

            free.Should().BeFalse();
        }
        finally
        {
            listener.Stop();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
