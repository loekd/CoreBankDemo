namespace CoreBankDemo.DemoRunner.Application.Ports;

public enum LoadWorkflowPhase
{
    Run,
    Wait,
    Assert,
    Investigate,
}

/// <summary>One named invariant result surfaced by the accepted load workflow (never invented locally).</summary>
public sealed record InvariantResult(string Name, bool Passed, string Detail);

/// <summary>
/// Outcome of the Run→Wait→Assert(→Investigate) workflow. This is a thin presentation
/// adapter over Story 7.3's accepted LoadTestSupport/k6 workflow — it never computes an
/// invariant itself, only relays what LoadTestSupport reports.
/// </summary>
public sealed record LoadWorkflowResult(
    LoadWorkflowPhase FailedAtPhase,
    bool Completed,
    bool AllPassed,
    IReadOnlyList<InvariantResult> Invariants,
    string? ErrorSummary)
{
    public static LoadWorkflowResult Success(IReadOnlyList<InvariantResult> invariants) =>
        new(LoadWorkflowPhase.Assert, true, invariants.All(i => i.Passed), invariants, null);

    public static LoadWorkflowResult PhaseFailure(LoadWorkflowPhase phase, string errorSummary, IReadOnlyList<InvariantResult>? invariants = null) =>
        new(phase, false, false, invariants ?? [], errorSummary);
}

/// <summary>
/// Runs the Story 7.3 accepted load workflow (reset → k6 → drain → assert) via
/// LoadTestSupport and reports its Run→Wait→Assert phases and five invariant results.
/// </summary>
public interface ILoadWorkflowRunner
{
    Task<LoadWorkflowResult> RunAsync(int? expectedUniqueCount, CancellationToken ct);
}
