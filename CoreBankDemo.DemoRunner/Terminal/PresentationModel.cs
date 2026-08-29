using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Application.StateMachine;

namespace CoreBankDemo.DemoRunner.Terminal;

/// <summary>
/// Immutable, renderer-agnostic presentation state. Terminal.Gui only ever reads these
/// records and emits intents back to <see cref="Application.StateMachine.SessionController"/>;
/// no scenario or process logic lives here (ADR-015). Building this model is deliberately
/// pure and unit-testable without a real terminal.
/// </summary>
public sealed record CueRowViewModel(string Id, string SlideAnchor, string Title, string StatusSymbol, string StatusText, bool IsCurrent);

public sealed record ConfidenceRowViewModel(string ResourceName, string Symbol, string Label);

public sealed record CurrentCueViewModel(
    string Title,
    string SlideAnchor,
    string SpeakerNote,
    string EvidenceSummary,
    bool CanPreArm,
    bool CanRun,
    bool CanRetry,
    bool CanNext,
    bool CanInvestigate,
    IReadOnlyList<InvariantResult>? LoadInvariants);

public sealed record SessionViewModel(
    string ScenarioName,
    string Mode,
    int CueNumber,
    int CueCount,
    IReadOnlyList<CueRowViewModel> Cues,
    CurrentCueViewModel Current,
    IReadOnlyList<ConfidenceRowViewModel> Confidence);

public static class PresentationModelBuilder
{
    public static SessionViewModel Build(SessionController controller, IReadOnlyDictionary<string, HealthStatus> confidence)
    {
        var state = controller.State;

        var cueRows = state.Cues.Select((c, i) => new CueRowViewModel(
                c.Definition.Id,
                c.Definition.SlideAnchor,
                c.Definition.Title,
                SymbolFor(c.Status),
                c.Status.ToString(),
                i == state.CurrentCueIndex))
            .ToList();

        var current = state.CurrentCue;
        var currentViewModel = new CurrentCueViewModel(
            current.Definition.Title,
            current.Definition.SlideAnchor,
            current.Definition.SpeakerNote,
            current.EvidenceSummary,
            CanPreArm: !state.IsBusy && current.Status == CueStatus.Available && current.Definition.PreArmActions.Count > 0,
            CanRun: !state.IsBusy && current.Status is CueStatus.Available or CueStatus.PreArmed,
            CanRetry: !state.IsBusy && current.Status is CueStatus.Failed or CueStatus.Ambiguous,
            CanNext: state.CanAdvanceToNext,
            CanInvestigate: current.Status == CueStatus.Failed && current.Definition.InvestigateActions.Count > 0,
            controller.LastLoadWorkflowResult?.Invariants);

        var confidenceRows = confidence.Select(kv => new ConfidenceRowViewModel(kv.Key, SymbolForHealth(kv.Value), kv.Key)).ToList();

        return new SessionViewModel(
            state.ScenarioName,
            state.Mode.ToString().ToUpperInvariant(),
            state.CurrentCueIndex + 1,
            state.Cues.Count,
            cueRows,
            currentViewModel,
            confidenceRows);
    }

    private static string SymbolFor(CueStatus status) => status switch
    {
        CueStatus.Passed => "✓",
        CueStatus.Locked => "○",
        CueStatus.Available => "○",
        CueStatus.PreArmed => "◐",
        CueStatus.Running => "▶",
        CueStatus.Failed => "✗",
        CueStatus.Ambiguous => "?",
        CueStatus.Cancelled => "!",
        _ => "?",
    };

    private static string SymbolForHealth(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "●",
        HealthStatus.Unhealthy => "✗",
        _ => "◐",
    };
}
