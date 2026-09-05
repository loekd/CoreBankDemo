using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Application.Ports;

/// <summary>
/// The outcome of reading the levels a topology is actually running under.
/// </summary>
/// <param name="Succeeded">False when neither the generated session config nor the checked-in profile could be read.</param>
/// <param name="Levels">Always populated — the checked-in defaults when a read failed, never an invented zero.</param>
/// <param name="FromGeneratedSession">True when the levels came from a config this console wrote.</param>
/// <param name="Path">The file the levels were read from, or attempted.</param>
/// <param name="ErrorSummary">Names the failed read, for the evidence strip.</param>
public sealed record FaultConfigReadResult(
    bool Succeeded,
    FaultLevels Levels,
    bool FromGeneratedSession,
    string Path,
    string? ErrorSummary);

public sealed record FaultConfigWriteResult(bool Succeeded, string Path, string? ErrorSummary);

/// <summary>
/// Writes and reads the generated Dev Proxy session configuration a topology's proxy watches.
/// Keeps the controller free of file I/O, matching the ports/adapters split every other
/// side effect in this console goes through.
/// </summary>
public interface IFaultInjector
{
    /// <summary>
    /// Reads the levels in force for a profile: the generated session config if present,
    /// otherwise the checked-in profile the AppHost would have started with.
    /// </summary>
    Task<FaultConfigReadResult> ReadAsync(TopologyProfile profile, CancellationToken ct);

    /// <summary>
    /// Writes every knob in one generated session config, seeded from the checked-in
    /// profile so plugin order, plugin path, port and urlsToWatch are inherited. Never
    /// writes a checked-in file.
    /// </summary>
    Task<FaultConfigWriteResult> WriteAsync(TopologyProfile profile, FaultLevels levels, CancellationToken ct);

    /// <summary>
    /// Rewrites the session config quiet before a topology is armed, so a config surviving
    /// from a prior session can never silently apply its levels to this one.
    /// </summary>
    Task<FaultConfigWriteResult> ResetAsync(TopologyProfile profile, CancellationToken ct);

    /// <summary>
    /// Removes the generated session config and its errors sibling. Called when this session
    /// stops owning a topology (Stop, the outgoing half of Switch, and quit) so the file never
    /// outlives the session and shadows the checked-in profile for a later non-console run
    /// (<c>aspire run</c>, a teammate's manual start), silently disabling the shipped presets.
    /// </summary>
    Task<FaultConfigWriteResult> DeleteAsync(TopologyProfile profile, CancellationToken ct);
}
