namespace CoreBankDemo.DemoRunner.Application.StateMachine;

public enum SessionMode
{
    Show,
    Rehearsal,
}

/// <summary>
/// Whole-session runtime state: the ordered cue list, which one is current, the single
/// in-flight dispatch guard, and topology ownership. The Terminal layer only ever reads
/// an immutable snapshot of this; only <see cref="SessionController"/> mutates it.
/// </summary>
public sealed class SessionState
{
    public required string RunId { get; init; }
    public required string ScenarioName { get; init; }
    public required string ScenarioVersion { get; init; }
    public required string SourceCommit { get; init; }
    public required SessionMode Mode { get; init; }
    public required IReadOnlyList<CueRuntimeState> Cues { get; init; }

    public int CurrentCueIndex { get; set; }

    /// <summary>True while exactly one action is in flight; guards duplicate dispatch.</summary>
    public bool IsBusy { get; set; }

    /// <summary>
    /// Active topologies keyed by profile name. More than one profile can be active at
    /// once (e.g. the Regular AppHost for the Inbox cue plus the LoadTests AppHost for
    /// the resilience-proof cue, per ADR-014's replicated-topology graph).
    /// </summary>
    public Dictionary<string, Ports.TopologyHandle> Topologies { get; } = new(StringComparer.Ordinal);

    public CueRuntimeState CurrentCue => Cues[CurrentCueIndex];

    public bool CanAdvanceToNext => CurrentCue.Status == CueStatus.Passed && CurrentCueIndex < Cues.Count - 1;
}
