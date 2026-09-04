using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Application.Ports;

public enum ResourceDispatchStatus
{
    Rejected,
    Dispatched,
    Ambiguous,
    Partial,
}

public sealed record ResourceCommandResult(
    ResourceDispatchStatus Status,
    string Detail,
    IReadOnlyList<string> AffectedInstances,
    IReadOnlyList<string> FailedInstances)
{
    public bool Dispatched => Status is ResourceDispatchStatus.Dispatched or ResourceDispatchStatus.Partial;
    public bool RequiresRefresh => Status is ResourceDispatchStatus.Ambiguous or ResourceDispatchStatus.Partial;

    public static ResourceCommandResult Rejected(string detail) =>
        new(ResourceDispatchStatus.Rejected, detail, [], []);
}

public sealed record TopologyDiscoveryResult(
    bool IsReachable,
    IReadOnlyList<TopologySnapshot> Snapshots,
    string? ErrorSummary)
{
    public static TopologyDiscoveryResult Unreachable(string error) => new(false, [], error);
    public static TopologyDiscoveryResult Success(IReadOnlyList<TopologySnapshot> snapshots) => new(true, snapshots, null);
}

public interface IAspireAdapter
{
    Task<TopologyDiscoveryResult> DiscoverAsync(CancellationToken ct);

    Task<TopologySnapshot> GetSnapshotAsync(TopologyProfile profile, CancellationToken ct);

    Task<ResourceCommandResult> ExecuteResourceCommandAsync(
        TopologyProfile profile,
        string resourceName,
        ResourceCommand command,
        CancellationToken ct);
}
