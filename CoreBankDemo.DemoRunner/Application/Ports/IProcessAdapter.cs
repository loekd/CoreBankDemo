using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Application.Ports;

public interface IProcessAdapter
{
    Task<TopologyHandle> StartOwnedAsync(TopologyProfile profile, CancellationToken ct);

    string GetRecentOutput(TopologyHandle handle);

    Task StopOwnedAsync(TopologyHandle handle, CancellationToken ct);
}
