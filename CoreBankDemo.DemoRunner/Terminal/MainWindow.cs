using System.Collections.ObjectModel;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Application.Scenarios;
using CoreBankDemo.DemoRunner.Application.StateMachine;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using AppTerminal = Terminal.Gui.App.Application;

namespace CoreBankDemo.DemoRunner.Terminal;

/// <summary>
/// The responsive three-pane cockpit shell. Renders <see cref="SessionViewModel"/> only
/// and emits intents onto <see cref="SessionController"/>; contains no scenario or
/// process logic (ADR-015). TextView is used read-only purely as a multi-line label —
/// no editing capability is exposed to the speaker.
/// </summary>
#pragma warning disable CS0618 // TextView/legacy Application.RequestStop: version pinned centrally (ADR-015); acceptable for this stable release.
public sealed class MainWindow : Window
{
    private const int CompactWidthThreshold = 100;

    private readonly SessionController _controller;
    private readonly IHealthMonitor _health;
    private readonly Func<Task> _onExitRequested;

    private readonly ListView _cueList = new();
    private readonly Label _currentTitle = new() { X = 1, Y = 0 };
    private readonly TextView _speakerNote = new() { X = 1, Y = 1, Height = 3, ReadOnly = true };
    private readonly TextView _evidence = new() { X = 1, Y = 4, Height = 4, ReadOnly = true };
    private readonly ListView _invariantsList = new() { X = 1, Y = 8 };
    private readonly Button _preArmButton = new() { Text = "Pre-arm", X = 1 };
    private readonly Button _runButton = new() { Text = "Run cue", IsDefault = true };
    private readonly Button _retryButton = new() { Text = "Retry" };
    private readonly Button _nextButton = new() { Text = "Next" };
    private readonly Button _investigateButton = new() { Text = "Investigate" };
    private readonly ListView _confidenceList = new();
    private readonly Label _headerLabel = new() { X = 1, Y = 0 };

    public MainWindow(SessionController controller, IHealthMonitor health, Func<Task> onExitRequested)
    {
        _controller = controller;
        _health = health;
        _onExitRequested = onExitRequested;

        Title = "CoreBankDemo — Presentation Console";

        var cuesPane = new FrameView { Title = "TALK CUES", X = 0, Y = 1, Width = Dim.Percent(25), Height = Dim.Fill(1) };
        cuesPane.Add(_cueList);
        _cueList.Width = Dim.Fill();
        _cueList.Height = Dim.Fill();

        var currentPane = new FrameView { Title = "CURRENT CUE", X = Pos.Right(cuesPane), Y = 1, Width = Dim.Percent(50), Height = Dim.Fill(1) };
        _preArmButton.Y = 13;
        _runButton.Y = 13;
        _runButton.X = Pos.Right(_preArmButton) + 1;
        _retryButton.Y = 13;
        _retryButton.X = Pos.Right(_runButton) + 1;
        _nextButton.Y = 13;
        _nextButton.X = Pos.Right(_retryButton) + 1;
        _investigateButton.Y = 13;
        _investigateButton.X = Pos.Right(_nextButton) + 1;
        _invariantsList.Width = Dim.Fill(1);
        _invariantsList.Height = 4;
        currentPane.Add(_currentTitle, _speakerNote, _evidence, _invariantsList, _preArmButton, _runButton, _retryButton, _nextButton, _investigateButton);

        var confidencePane = new FrameView { Title = "CONFIDENCE", X = Pos.Right(currentPane), Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1) };
        confidencePane.Add(_confidenceList);
        _confidenceList.Width = Dim.Fill();
        _confidenceList.Height = Dim.Fill();

        var statusBar = new StatusBar(
        [
            new Shortcut("Enter", "Run", () => Dispatch(RunAsync)),
            new Shortcut("Ctrl+R", "Retry", () => Dispatch(RetryAsync)),
            new Shortcut("N", "Next", () => Dispatch(NextAsync)),
            new Shortcut("I", "Investigate", () => Dispatch(InvestigateAsync)),
            new Shortcut("Q", "Quit", () => Dispatch(() => _onExitRequested())),
        ]);

        Add(_headerLabel, cuesPane, currentPane, confidencePane, statusBar);

        _preArmButton.Accepted += (_, e) => { e.Handled = true; Dispatch(PreArmAsync); };
        _runButton.Accepted += (_, e) => { e.Handled = true; Dispatch(RunAsync); };
        _retryButton.Accepted += (_, e) => { e.Handled = true; Dispatch(RetryAsync); };
        _nextButton.Accepted += (_, e) => { e.Handled = true; Dispatch(NextAsync); };
        _investigateButton.Accepted += (_, e) => { e.Handled = true; Dispatch(InvestigateAsync); };

        FrameChanged += (_, _) => ApplyCompactLayoutIfNeeded(cuesPane, currentPane, confidencePane);
    }

    /// <summary>Fire-and-forget dispatch of one UI intent; each handler already guards single-in-flight at the controller.</summary>
    private void Dispatch(Func<Task> action) => _ = action().ContinueWith(
        t =>
        {
            if (t.Exception is not null)
            {
                AppTerminal.Invoke(() => _evidence.Text = $"Unexpected error: {t.Exception.GetBaseException().Message}");
            }
        },
        TaskScheduler.Default);

    private async Task PreArmAsync()
    {
        await _controller.PreArmCurrentAsync(CancellationToken.None);
        await RefreshAsync();
    }

    private async Task RunAsync()
    {
        await _controller.RunCurrentAsync(CancellationToken.None);
        await RefreshAsync();
    }

    private async Task RetryAsync()
    {
        await _controller.RetryCurrentAsync(CancellationToken.None);
        await RefreshAsync();
    }

    private async Task NextAsync()
    {
        _controller.TryAdvanceToNext();
        await RefreshAsync();
    }

    private async Task InvestigateAsync()
    {
        await _controller.RunInvestigateAsync(CancellationToken.None);
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var confidence = new Dictionary<string, HealthStatus>(StringComparer.Ordinal);
        foreach (var resource in KnownResources.All)
        {
            confidence[resource] = await _health.CheckAsync(resource, CancellationToken.None);
        }

        var model = PresentationModelBuilder.Build(_controller, confidence);
        AppTerminal.Invoke(() => Render(model));
    }

    private void Render(SessionViewModel model)
    {
        _headerLabel.Text = $"{model.ScenarioName} — {model.Mode} — Cue {model.CueNumber}/{model.CueCount}";

        _cueList.SetSource(new ObservableCollection<string>(model.Cues.Select(c =>
            $"{c.StatusSymbol} {(c.IsCurrent ? "▶ " : "  ")}S{c.SlideAnchor} · {c.Title}")));

        _currentTitle.Text = $"Slide {model.Current.SlideAnchor} · {model.Current.Title}";
        _speakerNote.Text = model.Current.SpeakerNote;
        _evidence.Text = string.IsNullOrEmpty(model.Current.EvidenceSummary) ? "(no evidence yet)" : model.Current.EvidenceSummary;

        _invariantsList.SetSource(new ObservableCollection<string>(
            (model.Current.LoadInvariants ?? []).Select(i => $"{(i.Passed ? "✓" : "✗")} {i.Name}: {i.Detail}")));

        _preArmButton.Enabled = model.Current.CanPreArm;
        _runButton.Enabled = model.Current.CanRun;
        _retryButton.Enabled = model.Current.CanRetry;
        _nextButton.Enabled = model.Current.CanNext;
        _investigateButton.Enabled = model.Current.CanInvestigate;

        _confidenceList.SetSource(new ObservableCollection<string>(model.Confidence.Select(c => $"{c.Symbol} {c.Label}")));
    }

    /// <summary>Below the preferred width, collapse the three panes so no critical evidence is truncated.</summary>
    private void ApplyCompactLayoutIfNeeded(FrameView cues, FrameView current, FrameView confidence)
    {
        var compact = Frame.Width < CompactWidthThreshold;
        cues.Visible = !compact;
        confidence.Visible = !compact;
        current.Width = compact ? Dim.Fill() : Dim.Percent(50);
    }
}
#pragma warning restore CS0618
