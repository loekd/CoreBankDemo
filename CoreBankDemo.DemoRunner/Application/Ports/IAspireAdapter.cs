using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Application.Ports;

public sealed record ResourceCommandResult(bool Dispatched, string Detail);

public interface IAspireAdapter
{
    Task<IReadOnlyList<TopologySnapshot>> DiscoverAsync(CancellationToken ct);

    Task<TopologySnapshot> GetSnapshotAsync(TopologyProfile profile, CancellationToken ct);

    Task<ResourceCommandResult> ExecuteResourceCommandAsync(
        TopologyProfile profile,
        string resourceName,
        ResourceCommand command,
        CancellationToken ct);
}
