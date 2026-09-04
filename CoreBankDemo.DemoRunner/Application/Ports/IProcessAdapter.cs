using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Application.Ports;

public interface IProcessAdapter
{
    Task<TopologyHandle> StartOwnedAsync(TopologyProfile profile, CancellationToken ct);

    string GetRecentOutput(TopologyHandle handle);

    Task<OwnedStopResult> StopOwnedAsync(TopologyHandle handle, CancellationToken ct);

    Task ForgetExitedOwnedAsync(TopologyHandle handle, CancellationToken ct);
}

public sealed record OwnedStopResult(bool Forced, string Detail);
