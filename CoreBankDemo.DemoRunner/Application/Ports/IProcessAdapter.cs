using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Application.Ports;

public interface IProcessAdapter
{
    /// <summary>
    /// Starts an AppHost this session owns. <paramref name="armFaults"/> is a launch-time
    /// property — <c>Features:UseDevProxy</c> is read when the AppHost starts — so it can
    /// only ever be decided here, never on a topology already running.
    /// </summary>
    Task<TopologyHandle> StartOwnedAsync(TopologyProfile profile, bool armFaults, CancellationToken ct);

    string GetRecentOutput(TopologyHandle handle);

    Task<OwnedStopResult> StopOwnedAsync(TopologyHandle handle, CancellationToken ct);

    Task ForgetExitedOwnedAsync(TopologyHandle handle, CancellationToken ct);
}

public sealed record OwnedStopResult(bool Forced, string Detail);
