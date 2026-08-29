namespace CoreBankDemo.DemoRunner.Application.StateMachine;

/// <summary>
/// Lifecycle state of one talk cue. <see cref="Passed"/> is the only state from which
/// Next becomes available; every other state keeps Next unavailable (ADR-015).
/// </summary>
public enum CueStatus
{
    /// <summary>A prior cue has not yet passed; this cue cannot be selected.</summary>
    Locked,

    /// <summary>Selectable, not yet pre-armed or fired.</summary>
    Available,

    /// <summary>Pre-arm actions ran successfully; no mutating action has been sent.</summary>
    PreArmed,

    /// <summary>The cue's actions are executing; duplicate activation must be suppressed.</summary>
    Running,

    /// <summary>Evidence proved the cue; Next becomes available.</summary>
    Passed,

    /// <summary>A probe/assertion proved failure; Retry/Details/recovery are offered.</summary>
    Failed,

    /// <summary>The outcome could not be proven (e.g. timeout); must be reconciled, never advanced.</summary>
    Ambiguous,

    /// <summary>The run was interrupted while this cue was mid-flight; recovered, never Passed.</summary>
    Cancelled,
}

public sealed class CueRuntimeState(Scenarios.TalkCueDefinition definition)
{
    public Scenarios.TalkCueDefinition Definition { get; } = definition;
    public CueStatus Status { get; set; } = CueStatus.Locked;
    public string EvidenceSummary { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public DateTimeOffset? LastUpdatedAt { get; set; }

    /// <summary>Values captured from this cue's action responses, keyed by CaptureAs, for assertHttp comparisons.</summary>
    public Dictionary<string, string> Captures { get; } = new(StringComparer.Ordinal);
}
