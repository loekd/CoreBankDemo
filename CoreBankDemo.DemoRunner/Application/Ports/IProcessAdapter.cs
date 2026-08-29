namespace CoreBankDemo.DemoRunner.Application.Ports;

/// <summary>Result of an owned start or a verified attach to an Aspire AppHost profile.</summary>
public sealed record TopologyHandle(string ProfileName, bool IsOwned, int? ProcessId, string Fingerprint);

/// <summary>
/// Boundary for owning/attaching to the exact known Aspire AppHost profile a scenario
/// requires. Implementations must never target an arbitrary process path — only the
/// two known AppHost project paths (ADR-015).
/// </summary>
public interface IProcessAdapter
{
    /// <summary>Starts the given profile as a tracked child process tree, owned by this session.</summary>
    Task<TopologyHandle> StartOwnedAsync(string profileName, CancellationToken ct);

    /// <summary>
    /// Checks whether a healthy, fingerprint-matching topology for <paramref name="profileName"/>
    /// is already running on the documented ports. Returns null if no match is found; never
    /// returns a partially healthy or unrecognized graph as attachable.
    /// </summary>
    Task<TopologyHandle?> TryAttachAsync(string profileName, CancellationToken ct);

    /// <summary>Returns bounded, redacted recent output for an owned process.</summary>
    string GetRecentOutput(TopologyHandle handle);

    /// <summary>
    /// Stops only a handle this adapter itself started (<see cref="TopologyHandle.IsOwned"/>);
    /// attempts graceful cancellation before forced termination. A no-op for unowned handles.
    /// </summary>
    Task StopOwnedAsync(TopologyHandle handle, CancellationToken ct);
}
