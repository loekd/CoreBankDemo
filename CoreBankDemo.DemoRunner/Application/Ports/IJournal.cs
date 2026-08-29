using CoreBankDemo.DemoRunner.Application.StateMachine;

namespace CoreBankDemo.DemoRunner.Application.Ports;

/// <summary>
/// One fact-only journal entry (never secrets or unbounded raw logs). An interrupted
/// <see cref="CueStatus.Running"/> entry recovers as <see cref="CueStatus.Ambiguous"/> on
/// next launch — it is never upgraded to Passed from the journal alone (ADR-015).
/// </summary>
public sealed record JournalEntry(
    string Session,
    string ScenarioVersion,
    string SourceCommit,
    string SlideAnchor,
    string Cue,
    string? Phase,
    CueStatus State,
    DateTimeOffset Timestamp,
    string EvidenceSummary);

/// <summary>Append-only, gitignored local journal of session facts.</summary>
public interface IJournal
{
    Task AppendAsync(JournalEntry entry, CancellationToken ct);

    /// <summary>Returns the most recently journaled session for this scenario and mode prefix.</summary>
    Task<string?> TryReadLatestSessionAsync(string sessionPrefix, CancellationToken ct);

    /// <summary>Returns the last checkpoint recorded for this session, if any, for resume/recovery.</summary>
    Task<JournalEntry?> TryReadLastCheckpointAsync(string session, CancellationToken ct);
}
