namespace CoreBankDemo.DemoRunner.Application.Ports;

/// <summary>
/// A timestamped, provenance-labelled record of one fully successful rehearsal.
/// Never presented as a substitute for a live Passed cue — always rendered with its
/// REHEARSAL label, timestamp, source commit, and scenario version (ADR-015).
/// </summary>
public sealed record ProofPack(
    string ScenarioName,
    string ScenarioVersion,
    string SourceCommit,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ProofPackCueResult> CueResults,
    IReadOnlyList<InvariantResult> LoadInvariants);

public sealed record ProofPackCueResult(string CueId, string SlideAnchor, bool Passed, string EvidenceSummary);

/// <summary>
/// Stores/retrieves rehearsal proof packs. A proof pack is promoted to "last known good"
/// only after a full rehearsal (every cue, all five load invariants, cleanup) passes.
/// </summary>
public interface IProofPackStore
{
    Task SaveAsLatestKnownGoodAsync(ProofPack proofPack, CancellationToken ct);
    Task<ProofPack?> TryGetLatestKnownGoodAsync(CancellationToken ct);
}
