using System.Collections.ObjectModel;
using System.Drawing;
using System.Globalization;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Infrastructure;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using AppTerminal = Terminal.Gui.App.Application;

namespace CoreBankDemo.DemoRunner.Terminal;

#pragma warning disable CS0618
public sealed class MainWindow : Window
{
    private const int RailWidthPreferred = 22;
    private const int RailWidthCompact = 5;
    private const int ActionColumnWidth = 22;

    // Operations workspace column grid, sized so both columns still fit the
    // narrowest supported content area (80 columns minus the compact rail).
    private const int LabelX = 1;
    private const int LabelWidth = 13;
    private const int WideFieldWidth = 28;
    private const int SecondLabelX = 45;
    private const int SecondLabelWidth = 11;
    private const int NarrowFieldWidth = 10;

    // Faults workspace grid. The value column is never sacrificed to preserve the track:
    // the number is authoritative and the bar is reinforcement, so degradation drops the
    // bar first (see ApplyFaultsLayout) and the number never.
    private const int FaultLabelX = 1;
    private const int FaultLabelWidth = 15;
    private const int FaultTrackX = 17;
    private const int FaultTrackWidthPreferred = 24;
    private const int FaultTrackWidthCompact = 10;
    private const int MaximumFaultPresets = 3;

    private static readonly string[] NavigationLabels = ["Operations", "Resources", "Evidence", "Load Test", "Faults"];

    private readonly OperatorConsoleController _controller;
    private readonly TimeProvider _time;
    private readonly Func<Task> _onExitRequested;
    private readonly IConfirmationService _confirmation;
    private readonly bool _marshalUpdates;
    private readonly CancellationTokenSource _pollCancellation = new();
    private readonly CancellationTokenSource _sessionCancellation = new();
    private readonly object _activeActionsLock = new();
    private readonly HashSet<Task> _activeActions = [];

    private readonly Label _topologyBar = new() { X = 1, Y = 0, Height = 1, Width = Dim.Fill(22) };
    private readonly Button _aspireDashboardButton = NewButton("Aspire");
    private readonly Button _jaegerButton = NewButton("Jaeger");
    private readonly FrameView _navigation = new() { X = 0, Y = 1, Width = RailWidthPreferred, Height = Dim.Fill(3), Title = "WORKSPACES" };
    private readonly FrameView _content = new() { X = RailWidthPreferred, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(3) };
    private readonly Label _statusLine = new() { X = 1, Y = Pos.AnchorEnd(3), Height = 1, Width = Dim.Fill(1) };
    private readonly Label _messageLine = new() { X = 1, Y = Pos.AnchorEnd(2), Height = 1, Width = Dim.Fill(1) };

    private readonly Button[] _navigationButtons;
    private readonly View _operationsView;
    private readonly View _resourcesView;
    private readonly View _evidenceView;
    private readonly View _loadView;
    private readonly View _faultsView;
    private readonly View[] _workspaces;
    private View? _mountedWorkspace;

    private readonly TextField _fromAccount = new() { Text = "NL91ABNA0417164300" };
    private readonly TextField _toAccount = new() { Text = "NL20INGB0001234567" };
    private readonly TextField _amount = new() { Text = "1.00" };
    private readonly TextField _currency = new() { Text = "EUR" };
    // Unique per console session on purpose. An idempotency key is a permanent
    // identity: a hard-coded default meant every "Supplied" submit in a new
    // session silently replayed whatever row a previous session had created
    // under that key, so the operator saw hours-old state and no fresh payment.
    // Typing a fixed key to demonstrate a replay is still one keystroke away.
    private readonly TextField _suppliedKey = new() { Text = NewSessionKey() };
    private readonly TextField _outcomeKey = new();
    private readonly TextField _burstCount = new() { Text = "20" };
    private readonly TextField _burstConcurrency = new() { Text = "4" };
    private readonly Button _railButton = NewButton("Rail: standard");
    private readonly Button _idempotencyButton = NewButton("Idempotency: Generated");
    private readonly Button _submitButton = NewButton("Submit payment", isDefault: true);
    private readonly Button _resendButton = NewButton("Resend same key");
    private readonly Button _burstButton = NewButton("Run bounded burst");
    private readonly Button _cancelBurstButton = NewButton("Cancel active burst");
    private readonly Button _queryButton = NewButton("Query outcome (blank = selected row)");
    private readonly Label _burstStatus = new();
    private readonly Label _burstProvenStatus = new();
    // Where a submitted payment resolves in place. Bound through the shared ListBinding, which
    // preserves both the selection and the scroll offset, so an arriving outcome never moves
    // the list under an operator who may be mid-sentence with a finger on a row.
    private readonly ListView _paymentList = new();
    private readonly Label _feedStatus = new();
    private readonly Label _operationsHint = new();
    private Label _omittedNote = null!;
    private Label _burstCountLabel = null!;
    private Label _concurrencyLabel = null!;
    private Label _outcomeLabel = null!;

    private readonly ListView _resourceList = new();
    private readonly Button _startRegularButton = NewButton("Start Regular");
    private readonly Button _attachRegularButton = NewButton("Attach Regular");
    private readonly Button _startLoadButton = NewButton("Start LoadTests");
    private readonly Button _attachLoadButton = NewButton("Attach LoadTests");
    private readonly Button _stopButton = NewButton("Stop AppHost");
    private readonly Button _switchButton = NewButton("Switch topology");
    private readonly Button _resourceActionButton = NewButton("Resource action");
    private readonly Button _restartResourceButton = NewButton("Restart selected");
    private readonly Button _refreshButton = NewButton("Refresh state");
    private readonly Button _armingButton = NewButton("Faults: arming");
    private readonly Label _resourcesHint = new();

    private readonly ListView _evidenceList = new();
    private readonly TextView _evidenceDetail = new() { ReadOnly = true, WordWrap = false };
    private readonly Button _detailsButton = NewButton("Details");
    private readonly Button _wrapButton = NewButton("Wrap: off");
    private readonly Button _copyButton = NewButton("Copy detail");
    private readonly Button _exportButton = NewButton("Export session evidence");
    private readonly Button _inspectPaymentsOutbox = NewButton("Payments outbox");
    private readonly Button _inspectCoreBankInbox = NewButton("CoreBank inbox");

    private readonly Label _loadPhase = new() { Text = "Reset → Run → Wait → Assert → Investigate" };
    private readonly Label _loadStatus = new();
    private readonly ListView _loadResults = new();
    private readonly Button _runLoadButton = NewButton("Run accepted load workflow");
    private readonly TextField _expectedUnique = new() { Text = "100" };
    private readonly Label _loadHint = new();

    // Terminal.Gui 2.4.17 has no Slider<T>; LinearRange<int> is the range control.
    // LeftBounded fills from the left for the single-handle knobs; Closed carries the
    // latency band's two handles (floor and ceiling) on one track.
    private readonly LinearRange<int> _errorRateRange = NewKnob(FaultLevels.ErrorRateSteps, LinearRangeSpanKind.LeftBounded);
    private readonly LinearRange<int> _latencyRange = NewKnob(FaultLevels.LatencySteps, LinearRangeSpanKind.Closed);
    private readonly LinearRange<int> _throttleRange = NewKnob(FaultLevels.ThrottleSteps, LinearRangeSpanKind.LeftBounded);
    private readonly Label _errorRateValue = new();
    private readonly Label _latencyValue = new();
    private readonly Label _throttleValue = new();
    private readonly Label _presetLabel = new();
    private readonly Label _faultCostLabel = new();
    private readonly Button _applyFaultsButton = NewButton("Apply", isDefault: false);
    private readonly Button _panicOffButton = NewButton("0 Panic-off (all knobs to zero)");
    private readonly Label _faultsHint = new();
    private readonly List<Button> _presetButtons = [];
    private IReadOnlyList<FaultPreset> _presets = [];
    private bool _suppressKnobEvents;
    private int _faultTrackWidth = FaultTrackWidthPreferred;
    private int _faultsContentWidth = 100 - RailWidthPreferred - 4;
    private FaultKnobRow[] _knobRows = [];

    private readonly ListBinding _resourceBinding;
    private readonly ListBinding _evidenceBinding;
    private readonly ListBinding _loadResultBinding;
    private readonly ListBinding _paymentBinding;

    private PaymentRail _rail = PaymentRail.Standard;
    private IdempotencyMode _idempotencyMode = IdempotencyMode.Generated;
    private IReadOnlyList<ResourceRowViewModel> _resourceRows = [];
    private IReadOnlyList<EvidenceRowViewModel> _evidenceRows = [];
    private IReadOnlyList<PaymentRowViewModel> _paymentRows = [];
    private IReadOnlyList<long> _paymentLineOwners = [];
    private bool _paymentSelectionIsDeliberate;
    private bool _rebindingPaymentList;
    private bool _rebindingEvidenceList;
    private bool _compactLayout;
    private string _message = string.Empty;
    private long _messageMark = -1;

    public MainWindow(OperatorConsoleController controller, Func<Task> onExitRequested)
        : this(controller, onExitRequested, null, true)
    {
    }

    internal MainWindow(
        OperatorConsoleController controller,
        Func<Task> onExitRequested,
        IConfirmationService? confirmation,
        bool startPolling,
        bool marshalUpdates = true,
        TimeProvider? time = null)
    {
        OperatorTheme.Register();
        _controller = controller;
        _time = time ?? TimeProvider.System;
        _onExitRequested = onExitRequested;
        _confirmation = confirmation ?? new TerminalConfirmationService();
        _marshalUpdates = marshalUpdates;
        _resourceBinding = new ListBinding(_resourceList);
        _evidenceBinding = new ListBinding(_evidenceList);
        _loadResultBinding = new ListBinding(_loadResults);
        _paymentBinding = new ListBinding(_paymentList);
        Title = "CoreBankDemo — Operator Console";
        OperatorTheme.Apply(this, OperatorTheme.BaseScheme);
        OperatorTheme.Apply(_navigation, OperatorTheme.RailScheme);
        OperatorTheme.Apply(_submitButton, OperatorTheme.ActionScheme);
        OperatorTheme.Apply(_resendButton, OperatorTheme.ActionScheme);
        OperatorTheme.Apply(_burstButton, OperatorTheme.ActionScheme);
        OperatorTheme.Apply(_queryButton, OperatorTheme.ActionScheme);
        OperatorTheme.Apply(_resourceActionButton, OperatorTheme.DestructiveScheme);
        OperatorTheme.Apply(_restartResourceButton, OperatorTheme.DestructiveScheme);
        OperatorTheme.Apply(_stopButton, OperatorTheme.DestructiveScheme);
        OperatorTheme.Apply(_switchButton, OperatorTheme.DestructiveScheme);
        OperatorTheme.Apply(_runLoadButton, OperatorTheme.DestructiveScheme);
        OperatorTheme.Apply(_applyFaultsButton, OperatorTheme.ActionScheme);
        // The lock-exempt family, sharing one signature so the controls that stay live
        // while everything else dims read as a family at a glance.
        OperatorTheme.Apply(_cancelBurstButton, OperatorTheme.LockExemptScheme);
        OperatorTheme.Apply(_panicOffButton, OperatorTheme.LockExemptScheme);
        OperatorTheme.Apply(_errorRateRange, OperatorTheme.LockExemptScheme);
        OperatorTheme.Apply(_latencyRange, OperatorTheme.LockExemptScheme);
        OperatorTheme.Apply(_throttleRange, OperatorTheme.LockExemptScheme);

        _navigationButtons =
        [
            CreateNavigationButton(WorkspaceKind.Operations, 0),
            CreateNavigationButton(WorkspaceKind.Resources, 2),
            CreateNavigationButton(WorkspaceKind.Evidence, 4),
            CreateNavigationButton(WorkspaceKind.LoadTest, 6),
            CreateNavigationButton(WorkspaceKind.Faults, 8),
        ];
        _navigation.Add(_navigationButtons);

        _operationsView = BuildOperationsView();
        _resourcesView = BuildResourcesView();
        _evidenceView = BuildEvidenceView();
        _loadView = BuildLoadView();
        _faultsView = BuildFaultsView();
        _workspaces = [_operationsView, _resourcesView, _evidenceView, _loadView, _faultsView];

        var statusBar = new StatusBar(
        [
            new Shortcut("1", "Operations", () => ActivateWorkspace(WorkspaceKind.Operations)),
            new Shortcut("2", "Resources", () => ActivateWorkspace(WorkspaceKind.Resources)),
            new Shortcut("3", "Evidence", () => ActivateWorkspace(WorkspaceKind.Evidence)),
            new Shortcut("4", "Load Test", () => ActivateWorkspace(WorkspaceKind.LoadTest)),
            new Shortcut("5", "Faults", () => ActivateWorkspace(WorkspaceKind.Faults)),
            new Shortcut("0", "Panic-off", () => Dispatch(() => SurfaceAsync(_controller.PanicOffAsync(_sessionCancellation.Token)))),
            new Shortcut("R", "Refresh", () => Dispatch(() => _controller.RefreshAsync(_sessionCancellation.Token))),
            new Shortcut("Q", "Quit", () => Dispatch(RequestExitAsync)),
        ]);

        _aspireDashboardButton.X = Pos.AnchorEnd(21);
        _aspireDashboardButton.Y = 0;
        _jaegerButton.X = Pos.AnchorEnd(10);
        _jaegerButton.Y = 0;
        _aspireDashboardButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            OpenKnownLink("Aspire dashboard", KnownLinks.AspireDashboard);
        };
        _jaegerButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            OpenKnownLink("Jaeger", KnownLinks.Jaeger);
        };

        Add(_topologyBar, _aspireDashboardButton, _jaegerButton, _navigation, _content, _statusLine, _messageLine, statusBar);
        UpdateNavigationText();
        FrameChanged += (_, _) => ApplyResponsiveLayout();
        _controller.StateChanged += OnStateChanged;
        Render(PresentationModelBuilder.Build(_controller.State, _time.GetUtcNow()));
        if (startPolling)
        {
            _ = PollAsync(_pollCancellation.Token);
        }
    }

    private View BuildOperationsView()
    {
        var view = NewWorkspace("OPERATIONS");
        AddField(view, "From", _fromAccount, LabelX, 0, LabelWidth, WideFieldWidth);
        AddField(view, "To", _toAccount, LabelX, 1, LabelWidth, WideFieldWidth);
        AddField(view, "Amount", _amount, LabelX, 2, LabelWidth, NarrowFieldWidth);
        AddField(view, "Currency", _currency, SecondLabelX, 2, SecondLabelWidth, NarrowFieldWidth);
        AddField(view, "Supplied key", _suppliedKey, LabelX, 3, LabelWidth, WideFieldWidth);

        _railButton.X = LabelX;
        _railButton.Y = 4;
        _idempotencyButton.X = Pos.Right(_railButton) + 2;
        _idempotencyButton.Y = 4;
        _railButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            _rail = _rail == PaymentRail.Standard ? PaymentRail.Instant : PaymentRail.Standard;
            _railButton.Text = $"Rail: {_rail.ToString().ToLowerInvariant()}";
        };
        _idempotencyButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            _idempotencyMode = _idempotencyMode switch
            {
                IdempotencyMode.Generated => IdempotencyMode.Supplied,
                IdempotencyMode.Supplied => IdempotencyMode.Omitted,
                _ => IdempotencyMode.Generated,
            };
            _idempotencyButton.Text = $"Idempotency: {_idempotencyMode}";
        };

        _omittedNote = new Label { X = LabelX, Y = 5, Height = 1, Width = Dim.Fill(1), Text = "Omitted mode: not retry-safe after an ambiguous outcome." };
        view.Add(_omittedNote);

        _submitButton.X = LabelX;
        _submitButton.Y = 7;
        _resendButton.X = Pos.Right(_submitButton) + 2;
        _resendButton.Y = 7;
        _submitButton.Accepting += (_, e) => { e.Handled = true; Dispatch(SubmitPaymentAsync); };
        _resendButton.Accepting += (_, e) => { e.Handled = true; Dispatch(() => SurfaceAsync(_controller.ResendLastPaymentAsync(_sessionCancellation.Token))); };

        _burstCountLabel = AddField(view, "Burst count", _burstCount, LabelX, 9, LabelWidth, NarrowFieldWidth);
        _concurrencyLabel = AddField(view, "Concurrency", _burstConcurrency, SecondLabelX, 9, SecondLabelWidth, NarrowFieldWidth);
        _burstButton.X = LabelX;
        _burstButton.Y = 10;
        _cancelBurstButton.X = Pos.Right(_burstButton) + 2;
        _cancelBurstButton.Y = 10;
        _burstStatus.X = LabelX;
        _burstStatus.Y = 11;
        _burstStatus.Height = 1;
        _burstStatus.Width = Dim.Fill(1);
        _burstButton.Accepting += (_, e) => { e.Handled = true; Dispatch(RunBurstAsync); };
        _cancelBurstButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (!_controller.CancelActiveBurst())
            {
                ShowMessage("No active burst is available to cancel.");
            }
        };

        _burstProvenStatus.X = LabelX;
        _burstProvenStatus.Y = 12;
        _burstProvenStatus.Height = 1;
        _burstProvenStatus.Width = Dim.Fill(1);

        _outcomeLabel = AddField(view, "Outcome key", _outcomeKey, LabelX, 14, LabelWidth, WideFieldWidth);
        _queryButton.X = LabelX;
        _queryButton.Y = 15;
        _queryButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            Dispatch(() => SurfaceAsync(_controller.QueryOutcomeAsync(OutcomeQueryTarget(), _sessionCancellation.Token)));
        };

        _paymentList.X = LabelX;
        _paymentList.Y = 17;
        _paymentList.Width = Dim.Fill(1);
        _paymentList.Height = Dim.Fill(2);
        // Only an operator's own selection may stand in for a blank outcome field. SelectedItem
        // defaults to 0, so without this a blank field would quietly query the oldest payment.
        _paymentList.ValueChanged += (_, _) =>
        {
            if (!_rebindingPaymentList)
            {
                _paymentSelectionIsDeliberate = true;
            }
        };

        _feedStatus.X = LabelX;
        _feedStatus.Y = Pos.AnchorEnd(2);
        _feedStatus.Height = 1;
        _feedStatus.Width = Dim.Fill(1);

        LayoutHint(_operationsHint);
        view.Add(
            _railButton,
            _idempotencyButton,
            _submitButton,
            _resendButton,
            _burstButton,
            _cancelBurstButton,
            _burstStatus,
            _burstProvenStatus,
            _queryButton,
            _paymentList,
            _feedStatus,
            _operationsHint);
        return view;
    }

    private View BuildResourcesView()
    {
        var view = NewWorkspace("RESOURCES");
        _resourceList.X = 1;
        _resourceList.Y = 1;
        _resourceList.Width = Dim.Fill(ActionColumnWidth + 2);
        _resourceList.Height = Dim.Fill(2);

        // A vertical action column keeps every control on screen at 80 columns;
        // a horizontal button row pushed the right-most commands off the frame.
        var actions = new View
        {
            X = Pos.AnchorEnd(ActionColumnWidth + 1),
            Y = 1,
            Width = ActionColumnWidth,
            Height = Dim.Fill(2),
            CanFocus = true,
        };
        StackButtons(actions, 0, _startRegularButton, _attachRegularButton, _startLoadButton, _attachLoadButton);
        StackButtons(actions, 5, _stopButton, _switchButton, _resourceActionButton, _restartResourceButton, _refreshButton);
        StackButtons(actions, 11, _armingButton);

        _startRegularButton.Accepting += (_, e) => { e.Handled = true; Dispatch(() => SurfaceAsync(_controller.StartAsync(TopologyProfile.Regular, _sessionCancellation.Token))); };
        _attachRegularButton.Accepting += (_, e) => { e.Handled = true; Dispatch(() => SurfaceAsync(_controller.AttachAsync(TopologyProfile.Regular, _sessionCancellation.Token))); };
        _startLoadButton.Accepting += (_, e) => { e.Handled = true; Dispatch(() => SurfaceAsync(_controller.StartAsync(TopologyProfile.LoadTests, _sessionCancellation.Token))); };
        _attachLoadButton.Accepting += (_, e) => { e.Handled = true; Dispatch(() => SurfaceAsync(_controller.AttachAsync(TopologyProfile.LoadTests, _sessionCancellation.Token))); };
        _stopButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            IReadOnlyList<string> instance = _controller.OwnedProcessId is { } pid
                ? [$"{_controller.State.Profile} AppHost PID {pid}"]
                : [];
            if (ConfirmAndRestore(
                    new ConfirmationRequest("Stop owned AppHost", "aspire stop --apphost <exact known project>", instance),
                    _stopButton))
            {
                Dispatch(() => SurfaceAsync(_controller.StopAsync(_sessionCancellation.Token)));
            }
        };
        _switchButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            var target = _controller.State.Profile == TopologyProfile.Regular ? TopologyProfile.LoadTests : TopologyProfile.Regular;
            if (ConfirmAndRestore(
                    new ConfirmationRequest($"Switch to {target}", $"aspire stop current && aspire start {target}", [$"{_controller.State.Profile} AppHost", $"{target} AppHost"]),
                    _switchButton))
            {
                Dispatch(() => SurfaceAsync(_controller.SwitchAsync(target, _sessionCancellation.Token)));
            }
        };
        _resourceActionButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            TriggerSelectedResourceAction();
        };
        _restartResourceButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (_resourceRows.Count == 0)
            {
                ShowMessage("Select a verified resource before restarting.");
                return;
            }

            var index = Math.Clamp(_resourceList.SelectedItem ?? 0, 0, _resourceRows.Count - 1);
            var row = _resourceRows[index];
            if (!row.CanRestart)
            {
                ShowMessage($"{row.Name} cannot be restarted from the current fresh Aspire state.");
                return;
            }

            if (ConfirmAndRestore(
                    new ConfirmationRequest($"Restart {row.Name}", ExactCommands(row.Instances, "Restart"), row.Instances),
                    _restartResourceButton))
            {
                Dispatch(() => SurfaceAsync(_controller.ExecuteResourceCommandAsync(row.Name, ResourceCommand.Restart, _sessionCancellation.Token)));
            }
        };
        _refreshButton.Accepting += (_, e) => { e.Handled = true; Dispatch(() => _controller.RefreshAsync(_sessionCancellation.Token)); };
        _armingButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            Surface(_controller.SetArming(!_controller.State.FaultArmingRequested));
        };

        LayoutHint(_resourcesHint);
        view.Add(_resourceList, actions, _resourcesHint);
        return view;
    }

    private View BuildEvidenceView()
    {
        var view = NewWorkspace("EVIDENCE / RESULTS");
        _evidenceList.X = 1;
        _evidenceList.Y = 1;
        _evidenceList.Width = Dim.Percent(42);
        _evidenceList.Height = Dim.Fill(3);
        _evidenceDetail.X = Pos.Right(_evidenceList) + 1;
        _evidenceDetail.Y = 1;
        _evidenceDetail.Width = Dim.Fill(1);
        _evidenceDetail.Height = Dim.Fill(3);

        _detailsButton.X = 1;
        _detailsButton.Y = Pos.AnchorEnd(2);
        _wrapButton.X = Pos.Right(_detailsButton) + 1;
        _wrapButton.Y = Pos.AnchorEnd(2);
        _copyButton.X = Pos.Right(_wrapButton) + 1;
        _copyButton.Y = Pos.AnchorEnd(2);
        _exportButton.X = Pos.Right(_copyButton) + 1;
        _exportButton.Y = Pos.AnchorEnd(2);
        _inspectPaymentsOutbox.X = 1;
        _inspectPaymentsOutbox.Y = Pos.AnchorEnd(1);
        _inspectCoreBankInbox.X = Pos.Right(_inspectPaymentsOutbox) + 1;
        _inspectCoreBankInbox.Y = Pos.AnchorEnd(1);

        // Moving through the list is how an operator reads the journal, so the pane follows the
        // selection instead of waiting for Details to be pressed. Guarded against the rebind:
        // Bind restores the selection, and reacting to that would call back into the controller
        // on every render and pull the pane off whatever the operator had chosen.
        _evidenceList.ValueChanged += (_, _) =>
        {
            if (_rebindingEvidenceList || _evidenceRows.Count == 0)
            {
                return;
            }

            var index = Math.Clamp(_evidenceList.SelectedItem ?? 0, 0, _evidenceRows.Count - 1);
            _controller.SelectEvidence(_evidenceRows[index].Sequence);
        };

        _detailsButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (_evidenceRows.Count == 0)
            {
                ShowMessage("No action has been recorded this session yet.");
                return;
            }

            var index = Math.Clamp(_evidenceList.SelectedItem ?? 0, 0, _evidenceRows.Count - 1);
            _controller.SelectEvidence(_evidenceRows[index].Sequence);
            // The pane is the point of the button, so put the reader in it.
            _evidenceDetail.SetFocus();
        };
        _wrapButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            _evidenceDetail.WordWrap = !_evidenceDetail.WordWrap;
            _wrapButton.Text = _evidenceDetail.WordWrap ? "Wrap: on" : "Wrap: off";
        };
        _copyButton.Accepting += (_, e) => { e.Handled = true; CopyDetailToTerminalClipboard(); };
        _exportButton.Accepting += (_, e) => { e.Handled = true; Dispatch(() => SurfaceAsync(_controller.ExportEvidenceAsync(_sessionCancellation.Token))); };
        _inspectPaymentsOutbox.Accepting += (_, e) => { e.Handled = true; Dispatch(() => SurfaceAsync(_controller.InspectAsync(KnownEndpoints.PaymentsOutbox, _sessionCancellation.Token))); };
        _inspectCoreBankInbox.Accepting += (_, e) => { e.Handled = true; Dispatch(() => SurfaceAsync(_controller.InspectAsync(KnownEndpoints.CoreBankInbox, _sessionCancellation.Token))); };

        view.Add(_evidenceList, _evidenceDetail, _detailsButton, _wrapButton, _copyButton, _exportButton, _inspectPaymentsOutbox, _inspectCoreBankInbox);
        return view;
    }

    private View BuildLoadView()
    {
        var view = NewWorkspace("LOAD TEST");
        _loadPhase.X = 1;
        _loadPhase.Y = 1;
        _loadPhase.Height = 1;
        _loadPhase.Width = Dim.Fill(1);
        _loadStatus.X = 1;
        _loadStatus.Y = 2;
        _loadStatus.Height = 1;
        _loadStatus.Width = Dim.Fill(1);
        _loadResults.X = 1;
        _loadResults.Y = 4;
        _loadResults.Width = Dim.Fill(1);
        _loadResults.Height = Dim.Fill(4);

        // The expected-unique row sits below the results list: when it shared the
        // list's rows the list painted over it and the field was unreachable.
        AddField(view, "Expected unique", _expectedUnique, 1, Pos.AnchorEnd(3), 16, NarrowFieldWidth);
        _runLoadButton.X = 1;
        _runLoadButton.Y = Pos.AnchorEnd(2);
        _runLoadButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (!int.TryParse(_expectedUnique.Text.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value <= 0)
            {
                ShowMessage("Expected unique count must be a positive integer; load run not started.");
                return;
            }

            if (ConfirmAndRestore(
                    new ConfirmationRequest("Run accepted load workflow", "Reset → Run → Wait → Assert → Investigate", [KnownResources.LoadTestSupport, KnownResources.K6]),
                    _runLoadButton))
            {
                Dispatch(() => SurfaceAsync(_controller.RunLoadTestAsync(value, _sessionCancellation.Token)));
            }
        };
        LayoutHint(_loadHint);
        view.Add(_loadPhase, _loadStatus, _loadResults, _runLoadButton, _loadHint);
        return view;
    }

    /// <summary>
    /// The Faults workspace: three knobs above, the primary action anchored in the same fixed
    /// lower region every other workspace uses, and panic-off beside it. Follows
    /// <see cref="BuildLoadView"/>'s shape — a fixed set of labelled controls on the one
    /// continuous surface, no boxed sub-panel and no nested navigation.
    /// </summary>
    private View BuildFaultsView()
    {
        var view = NewWorkspace("FAULTS");
        var presetCaption = new Label { X = FaultLabelX, Y = 1, Height = 1, Width = FaultLabelWidth, Text = "Presets" };
        view.Add(presetCaption);
        for (var index = 0; index < MaximumFaultPresets; index++)
        {
            var button = NewButton(string.Empty);
            button.Y = 1;
            button.Visible = false;
            var slot = index;
            button.Accepting += (_, e) =>
            {
                e.Handled = true;
                // A preset only ever stages. It goes through the identical Apply path as a
                // hand-dragged value, so there is no second way to change the running system.
                if (slot < _presets.Count)
                {
                    Surface(_controller.StageFaults(_presets[slot].Levels));
                }
            };
            _presetButtons.Add(button);
            view.Add(button);
        }

        _presetLabel.Y = 3;
        _presetLabel.X = FaultLabelX;
        _presetLabel.Height = 1;
        _presetLabel.Width = Dim.Fill(1);
        _faultCostLabel.Y = 11;
        _faultCostLabel.X = FaultLabelX;
        _faultCostLabel.Height = 1;
        _faultCostLabel.Width = Dim.Fill(1);
        view.Add(_presetLabel, _faultCostLabel);

        _knobRows =
        [
            AddKnob(view, FaultKnobs.ErrorRate, _errorRateRange, _errorRateValue, 5),
            AddKnob(view, FaultKnobs.LatencyBand, _latencyRange, _latencyValue, 7),
            AddKnob(view, FaultKnobs.Throttling, _throttleRange, _throttleValue, 9),
        ];

        _errorRateRange.ValueChanged += (_, _) => OnKnobChanged();
        _latencyRange.ValueChanged += (_, _) => OnKnobChanged();
        _throttleRange.ValueChanged += (_, _) => OnKnobChanged();

        // The band's keyboard path is taken over deliberately. Terminal.Gui's own bindings
        // drive only one handle of a Closed range (Ctrl+arrow moves the same handle plain
        // arrow does), and the first arrow press after a programmatic Value assignment snaps
        // that handle to the ladder minimum because the control's focused-option index was
        // never synced. Both would be visible on stage as the bar disagreeing with the number.
        // Handling the keys here makes both handles reachable and deterministic; the mouse
        // still drives the control directly through ValueChanged.
        _latencyRange.KeyBindings.Remove(Key.CursorLeft.WithCtrl);
        _latencyRange.KeyBindings.Remove(Key.CursorRight.WithCtrl);
        _latencyRange.KeyDown += (_, key) => OnLatencyKey(key);

        // Panic-off is reachable before Apply on purpose: the recovery control comes first in
        // the tab order and the destructive-in-spirit control is last.
        _panicOffButton.X = FaultLabelX;
        _panicOffButton.Y = Pos.AnchorEnd(2);
        _applyFaultsButton.X = Pos.Right(_panicOffButton) + 2;
        _applyFaultsButton.Y = Pos.AnchorEnd(2);
        _panicOffButton.Accepting += (_, e) => { e.Handled = true; PanicOff(); };
        _applyFaultsButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            Dispatch(() => SurfaceAsync(_controller.ApplyFaultsAsync(_sessionCancellation.Token)));
        };

        LayoutHint(_faultsHint);
        view.Add(_panicOffButton, _applyFaultsButton, _faultsHint);
        return view;
    }

    private static FaultKnobRow AddKnob(View parent, string caption, LinearRange<int> range, Label value, int y)
    {
        var label = new Label { X = FaultLabelX, Y = y, Height = 1, Width = FaultLabelWidth, Text = caption };
        range.X = FaultTrackX;
        range.Y = y;
        range.Height = 1;
        range.Width = FaultTrackWidthPreferred;
        value.X = FaultTrackX + FaultTrackWidthPreferred + 2;
        value.Y = y;
        value.Height = 1;
        value.Width = Dim.Fill(1);
        parent.Add(label, range, value);
        return new FaultKnobRow(caption, label, value, range);
    }

    /// <summary>One rendered knob row, paired with the view-model name it draws.</summary>
    private sealed record FaultKnobRow(string Name, Label Caption, Label Value, LinearRange<int> Range);

    /// <summary>
    /// Stages whatever the knobs now read. Moving a knob never touches the running system:
    /// escalation is two-step (stage, then Apply), de-escalation is the single <c>0</c> key.
    /// </summary>
    private void OnKnobChanged()
    {
        if (_suppressKnobEvents)
        {
            return;
        }

        var latency = _latencyRange.Value;
        var staged = new FaultLevels(
            _errorRateRange.Value.End,
            latency.Start,
            latency.End,
            _throttleRange.Value.End);
        var result = _controller.StageFaults(staged);
        if (!result.Succeeded)
        {
            ShowMessage(result.Message);
        }
    }

    /// <summary>
    /// The latency band's keyboard contract: arrows move the <b>floor</b> by one ladder step
    /// and <c>Shift</c>+arrow by a coarse one; <c>Ctrl</c>+arrow moves the <b>ceiling</b>, and
    /// <c>Ctrl</c>+<c>Shift</c>+arrow coarsely; <c>Home</c> drops the floor to zero and
    /// <c>End</c> raises the ceiling to its maximum. Like every other knob movement it only
    /// ever stages.
    /// </summary>
    private void OnLatencyKey(Key key)
    {
        const int coarse = 3;
        var staged = _controller.State.Staged;
        var floor = staged.LatencyFloorMs;
        var ceiling = staged.LatencyCeilingMs;

        if (key == Key.CursorLeft) { floor = Step(floor, -1); }
        else if (key == Key.CursorRight) { floor = Step(floor, 1); }
        else if (key == Key.CursorLeft.WithShift) { floor = Step(floor, -coarse); }
        else if (key == Key.CursorRight.WithShift) { floor = Step(floor, coarse); }
        else if (key == Key.CursorLeft.WithCtrl) { ceiling = Step(ceiling, -1); }
        else if (key == Key.CursorRight.WithCtrl) { ceiling = Step(ceiling, 1); }
        else if (key == Key.CursorLeft.WithCtrl.WithShift) { ceiling = Step(ceiling, -coarse); }
        else if (key == Key.CursorRight.WithCtrl.WithShift) { ceiling = Step(ceiling, coarse); }
        else if (key == Key.Home) { floor = FaultLevels.LatencySteps[0]; }
        else if (key == Key.End) { ceiling = FaultLevels.LatencySteps[^1]; }
        else { return; }

        key.Handled = true;
        // Normalized() orders the band, so pushing the floor past the ceiling swaps them
        // rather than producing a range that reads backwards.
        Surface(_controller.StageFaults(staged with { LatencyFloorMs = floor, LatencyCeilingMs = ceiling }));
    }

    private static int Step(int value, int by)
    {
        var steps = FaultLevels.LatencySteps;
        var index = IndexOfStep(steps, value);
        return index < 0 ? value : steps[Math.Clamp(index + by, 0, steps.Count - 1)];
    }

    private void PanicOff() =>
        Dispatch(() => SurfaceAsync(_controller.PanicOffAsync(_sessionCancellation.Token)));

    private void Surface(CommandResult result)
    {
        if (!result.Succeeded)
        {
            ShowMessage(result.Message);
        }
    }

    private void RenderFaults(FaultsViewModel faults)
    {
        _presets = faults.Presets;
        var hidden = LayoutPresetChips(faults);
        _presetLabel.Text = hidden == 0
            ? $"Selected: {faults.PresetLabel}"
            // Never silently truncated: a preset the operator cannot see is a preset they
            // will assume does not exist.
            : $"Selected: {faults.PresetLabel} · {hidden} more preset{(hidden == 1 ? string.Empty : "s")} "
              + "not shown at this width";

        // Driven by the view model rather than by index, so adding or reordering a knob
        // surfaces as a message instead of silently mislabelling a row.
        foreach (var row in _knobRows)
        {
            var knob = faults.Knobs.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, row.Name, StringComparison.Ordinal));
            if (knob is null)
            {
                ShowMessage($"The Faults workspace has no level for '{row.Name}'.");
                continue;
            }

            row.Caption.Text = knob.Name;
            row.Value.Text = knob.ValueText;
        }

        // Rendering the staged position back onto the knobs must not look like the operator
        // moved them, or every render would re-stage and Apply could never settle.
        _suppressKnobEvents = true;
        try
        {
            SetKnob(_errorRateRange, FaultLevels.ErrorRateSteps, LinearRangeSpanKind.LeftBounded, 0, faults.Staged.ErrorRatePercent);
            SetKnob(_latencyRange, FaultLevels.LatencySteps, LinearRangeSpanKind.Closed, faults.Staged.LatencyFloorMs, faults.Staged.LatencyCeilingMs);
            SetKnob(_throttleRange, FaultLevels.ThrottleSteps, LinearRangeSpanKind.LeftBounded, 0, faults.Staged.ThrottleRequestsPerWindow);
        }
        finally
        {
            _suppressKnobEvents = false;
        }

        // Deliberately not gated on model.IsBusy: the fault controls are the console's second
        // named exemption from the single-action-in-flight lock, and raising a level while a
        // burst is running is the whole reason the capability exists.
        foreach (var row in _knobRows)
        {
            row.Range.Enabled = faults.Available;
        }

        _applyFaultsButton.Enabled = faults.CanApply;
        _applyFaultsButton.Text = faults.ApplyCaption;
        _panicOffButton.Enabled = faults.Available;
        _faultCostLabel.Text = faults.CostNote;
        _faultsHint.Text = Hint(faults.Available
            ? faults.Detail
            : $"{faults.DisabledReason} Levels shown are what would be applied.");
    }

    /// <summary>
    /// Places the preset chips left to right, wrapping to a second row rather than hiding a
    /// preset, and returns how many still did not fit so the caller can say so out loud.
    /// </summary>
    private int LayoutPresetChips(FaultsViewModel faults)
    {
        var left = FaultLabelX + FaultLabelWidth;
        var x = left;
        var y = 1;
        var hidden = Math.Max(0, faults.Presets.Count - _presetButtons.Count);
        for (var index = 0; index < _presetButtons.Count; index++)
        {
            var button = _presetButtons[index];
            if (index >= faults.Presets.Count)
            {
                button.Visible = false;
                button.Enabled = false;
                continue;
            }

            var name = faults.Presets[index].Name;
            var width = name.Length + 6;
            if (x + width > _faultsContentWidth && x > left)
            {
                x = left;
                y++;
            }

            if (y > 2 || x + width > _faultsContentWidth)
            {
                // Two chip rows is the budget before the knobs would be pushed off screen.
                button.Visible = false;
                button.Enabled = false;
                hidden++;
                continue;
            }

            button.Text = name;
            button.X = x;
            button.Y = y;
            button.Visible = true;
            button.Enabled = faults.Available;
            x += width;
        }

        return hidden;
    }

    /// <summary>
    /// Moves a knob's handles onto the ladder positions for the given values. Values always
    /// arrive normalized, so an off-ladder one is a bug rather than input: it is left alone
    /// and reported instead of being silently drawn at the floor while the label beside the
    /// track prints the true number — a disagreement the operator would read as the bar lying.
    /// </summary>
    private void SetKnob(
        LinearRange<int> range,
        IReadOnlyList<int> steps,
        LinearRangeSpanKind kind,
        int start,
        int end)
    {
        var startIndex = IndexOfStep(steps, start);
        var endIndex = IndexOfStep(steps, end);
        if (startIndex < 0 || endIndex < 0)
        {
            ShowMessage($"Fault level {(startIndex < 0 ? start : end)} is not a slider position; "
                + "the bar is left where it was and the printed value is authoritative.");
            return;
        }

        range.Value = new LinearRangeSpan<int>(kind, steps[startIndex], steps[endIndex], startIndex, endIndex);
    }

    private static int IndexOfStep(IReadOnlyList<int> steps, int value)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            if (steps[index] == value)
            {
                return index;
            }
        }

        return -1;
    }

    private static LinearRange<int> NewKnob(IReadOnlyList<int> steps, LinearRangeSpanKind kind)
    {
        var range = new LinearRange<int>([.. steps], Orientation.Horizontal)
        {
            RangeKind = kind,
            RangeAllowSingle = true,
            ShowLegends = false,
            ShowEndSpacing = false,
            MinimumInnerSpacing = 0,
            AllowEmpty = false,
        };

        // Terminal.Gui binds arrow to one step and Home/End to the knob's floor/ceiling out
        // of the box, but leaves Shift+arrow free (its Ctrl+arrow moves the *other* handle,
        // which is a different gesture). Three steps is the coarse move.
        range.KeyBindings.Add(Key.CursorLeft.WithShift, Command.Left, Command.Left, Command.Left);
        range.KeyBindings.Add(Key.CursorRight.WithShift, Command.Right, Command.Right, Command.Right);
        return range;
    }

    private async Task SubmitPaymentAsync()
    {
        if (!decimal.TryParse(_amount.Text.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
        {
            ShowMessage("Amount must be a decimal using '.' as the separator.");
            return;
        }

        var request = new PaymentRequest(
            _fromAccount.Text.ToString() ?? string.Empty,
            _toAccount.Text.ToString() ?? string.Empty,
            amount,
            _currency.Text.ToString() ?? string.Empty,
            _rail);
        await SurfaceAsync(_controller.SubmitPaymentAsync(
            request,
            _idempotencyMode,
            _idempotencyMode == IdempotencyMode.Supplied ? _suppliedKey.Text.ToString() : null,
            _sessionCancellation.Token));
    }

    private async Task RunBurstAsync()
    {
        if (!decimal.TryParse(_amount.Text.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            || !int.TryParse(_burstCount.Text.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || !int.TryParse(_burstConcurrency.Text.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var concurrency))
        {
            ShowMessage("Burst requires a valid decimal amount, positive count, and positive concurrency.");
            return;
        }

        var request = new PaymentRequest(
            _fromAccount.Text.ToString() ?? string.Empty,
            _toAccount.Text.ToString() ?? string.Empty,
            amount,
            _currency.Text.ToString() ?? string.Empty,
            _rail);
        await SurfaceAsync(_controller.RunBurstAsync(request, count, concurrency, _sessionCancellation.Token));
    }

    /// <summary>
    /// Terminal.Gui raises <c>Accepting</c> for a mouse click on any button but raises
    /// <c>Accepted</c> only for the default button, so every command here handles
    /// <c>Accepting</c> and marks it handled to stop the command bubbling to the default.
    /// </summary>
    private Button CreateNavigationButton(WorkspaceKind workspace, int y)
    {
        var button = NewButton(string.Empty);
        button.X = 1;
        button.Y = y;
        button.Width = Dim.Fill(1);
        button.Accepting += (_, e) =>
        {
            e.Handled = true;
            ActivateWorkspace(workspace);
        };
        return button;
    }

    /// <summary>
    /// Creates a button without the default drop shadow. The shadow occupies the row
    /// below and the column right of the button, which silently overwrote neighbouring
    /// fields and pushed bottom-anchored controls outside their frame.
    /// </summary>
    private static string NewSessionKey() => $"demo-key-{Guid.NewGuid():N}"[..17];

    private static Button NewButton(string text, bool isDefault = false) =>
        new() { Text = text, IsDefault = isDefault, ShadowStyle = ShadowStyles.None };

    private static View NewWorkspace(string title) =>
        new FrameView { Title = title, X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };

    private static void StackButtons(View parent, int startY, params Button[] buttons)
    {
        for (var index = 0; index < buttons.Length; index++)
        {
            buttons[index].X = 0;
            buttons[index].Y = startY + index;
            buttons[index].Width = Dim.Fill();
            parent.Add(buttons[index]);
        }
    }

    private static void LayoutHint(Label hint)
    {
        hint.X = 1;
        hint.Y = Pos.AnchorEnd(1);
        hint.Height = 1;
        hint.Width = Dim.Fill(1);
    }

    private static Label AddField(View parent, string label, TextField field, int x, Pos y, int labelWidth, int fieldWidth)
    {
        var caption = new Label { X = x, Y = y, Height = 1, Width = labelWidth, Text = label };
        parent.Add(caption);
        field.X = x + labelWidth + 1;
        field.Y = y;
        field.Height = 1;
        field.Width = fieldWidth;
        parent.Add(field);
        return caption;
    }

    private void ActivateWorkspace(WorkspaceKind workspace) => _controller.SelectWorkspace(workspace);

    private void OnStateChanged(OperatorConsoleState state) =>
        RunOnUiThread(() => Render(PresentationModelBuilder.Build(state, _time.GetUtcNow())));

    private void RunOnUiThread(Action action)
    {
        if (_marshalUpdates)
        {
            AppTerminal.Invoke(action);
        }
        else
        {
            action();
        }
    }

    public async Task RefreshAsync()
    {
        await _controller.InitializeAsync(_sessionCancellation.Token);
        Render(PresentationModelBuilder.Build(_controller.State, _time.GetUtcNow()));
    }

    /// <summary>
    /// Starts the first preflight without blocking the first paint, so the operator sees
    /// the console immediately instead of an empty terminal while discovery runs.
    /// </summary>
    internal void BeginInitialRefresh() => Dispatch(RefreshAsync);

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
                await _controller.RefreshAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ShowMessage($"Unreachable — {ex.Message}");
            }
        }
    }

    private void Render(OperatorPresentationModel model)
    {
        _topologyBar.Text = model.TopologyBar;
        MountWorkspace(model.ActiveWorkspace);
        UpdateNavigationText();

        _resourceRows = model.Resources;
        _resourceBinding.Bind(model.Resources.Count == 0
            ? ["○ No verified resources — refresh or attach a known topology"]
            : [.. model.Resources.Select(row => $"{row.Symbol} {row.Name,-20} {row.State,-11} {row.Detail} [{row.NextAction}]")]);
        _evidenceRows = model.Evidence;
        _rebindingEvidenceList = true;
        try
        {
            _evidenceBinding.Bind(model.Evidence.Count == 0
                ? ["○ No actions yet this session"]
                : [.. model.Evidence.Select(row => $"{row.Summary} · {row.Provenance}")]);
        }
        finally
        {
            _rebindingEvidenceList = false;
        }

        if (!string.Equals(_evidenceDetail.Text, model.SelectedEvidenceDetail, StringComparison.Ordinal))
        {
            _evidenceDetail.Text = model.SelectedEvidenceDetail;
        }

        _paymentRows = model.Payments;
        _rebindingPaymentList = true;
        try
        {
            _paymentBinding.Bind(model.Payments.Count == 0
                ? ["○ No payments submitted this session"]
                : [.. BuildPaymentLines(model.Payments)]);
        }
        finally
        {
            _rebindingPaymentList = false;
        }

        _feedStatus.Text = model.FeedStatus;

        _statusLine.Text = model.MutationStatus;
        RenderMessageLine(model);
        _burstStatus.Text = model.BurstStatus;
        _burstProvenStatus.Text = model.BurstProvenStatus;
        _loadStatus.Text = model.LoadPhaseStatus;
        _loadResultBinding.Bind(model.LoadResults);

        _operationsHint.Text = Hint(model.OperationsHint);
        _resourcesHint.Text = Hint(model.ResourcesHint);
        _loadHint.Text = Hint(model.LoadHint);
        RenderFaults(model.Faults);
        _armingButton.Text = model.ArmingCaption;
        _armingButton.Enabled = model.CanChangeArming;

        _submitButton.Enabled = !model.IsBusy;
        _resendButton.Enabled = model.CanResend;
        _burstButton.Enabled = !model.IsBusy;
        _cancelBurstButton.Enabled = model.CanCancelBurst;
        var state = _controller.State;
        _startRegularButton.Enabled = !model.IsBusy && state.Preflight?.CanStart(TopologyProfile.Regular) == true;
        _attachRegularButton.Enabled = !model.IsBusy
            && state.Ownership == TopologyOwnership.None
            && state.Preflight is not null
            && state.Preflight.Profiles.TryGetValue(TopologyProfile.Regular, out var regularProfile)
            && regularProfile.CanAttach;
        _startLoadButton.Enabled = !model.IsBusy && state.Preflight?.CanStart(TopologyProfile.LoadTests) == true;
        _attachLoadButton.Enabled = !model.IsBusy
            && state.Ownership == TopologyOwnership.None
            && state.Preflight is not null
            && state.Preflight.Profiles.TryGetValue(TopologyProfile.LoadTests, out var loadProfile)
            && loadProfile.CanAttach;
        _stopButton.Enabled = model.CanStopOrSwitch;
        _switchButton.Enabled = model.CanStopOrSwitch;
        _resourceActionButton.Enabled = !model.IsBusy && model.Resources.Any(row => row.CanMutate);
        _restartResourceButton.Enabled = !model.IsBusy && model.Resources.Any(row => row.CanRestart);
        _runLoadButton.Enabled = model.CanUseLoadTest && _controller.CanRunLoadTest;
        _queryButton.Enabled = true;
        _aspireDashboardButton.Enabled = _controller.State.Topology?.DashboardUrl is not null;
        _jaegerButton.Enabled = _controller.State.Profile != TopologyProfile.None;
        _detailsButton.Enabled = true;
        _wrapButton.Enabled = true;
    }

    /// <summary>
    /// Keeps only the active workspace in the view tree. Terminal.Gui resolves a mouse click
    /// to the last stacked sibling whose frame contains the point, so leaving all four
    /// workspaces mounted on top of each other sent every content click to the Load Test
    /// workspace and left the other workspaces' buttons unreachable by mouse.
    /// </summary>
    private void MountWorkspace(WorkspaceKind active)
    {
        var target = _workspaces[(int)active];
        foreach (var workspace in _workspaces)
        {
            workspace.Visible = ReferenceEquals(workspace, target);
        }

        if (ReferenceEquals(_mountedWorkspace, target))
        {
            return;
        }

        if (_mountedWorkspace is not null)
        {
            _content.Remove(_mountedWorkspace);
        }

        _content.Add(target);
        _mountedWorkspace = target;
    }

    /// <summary>
    /// Flattens the payment rows into Activity-row lines: a bold verb/object headline, its
    /// muted detail beneath, and the balance legs in a fixed column. The order is submission
    /// order and is never re-sorted, so a row that resolves stays exactly where it was.
    /// </summary>
    private IReadOnlyList<string> BuildPaymentLines(IReadOnlyList<PaymentRowViewModel> payments)
    {
        var lines = new List<string>();
        var owners = new List<long>();
        foreach (var payment in payments)
        {
            lines.Add($"{payment.Symbol} {payment.Headline}");
            owners.Add(payment.Sequence);
            lines.Add($"    {payment.Meta}");
            owners.Add(payment.Sequence);
            foreach (var leg in payment.Legs)
            {
                lines.Add($"    {leg}");
                owners.Add(payment.Sequence);
            }

            if (payment.LegSummary.Length > 0)
            {
                lines.Add($"    {payment.LegSummary}");
                owners.Add(payment.Sequence);
            }

            if (payment.Remedy.Length > 0)
            {
                lines.Add($"    {payment.Remedy}");
                owners.Add(payment.Sequence);
            }
        }

        _paymentLineOwners = owners;
        return lines;
    }

    /// <summary>
    /// What the outcome query looks up: whatever the operator typed, or — when the field is
    /// empty and a row was <i>deliberately</i> selected — that row's transaction id. The
    /// one-step remedy a row with an unknown outcome names is therefore one step away, while a
    /// blank field with no chosen row never quietly queries the oldest payment.
    /// </summary>
    internal string OutcomeQueryTarget()
    {
        var typed = _outcomeKey.Text.ToString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(typed))
        {
            return typed;
        }

        if (!_paymentSelectionIsDeliberate || _paymentRows.Count == 0 || _paymentLineOwners.Count == 0)
        {
            return typed;
        }

        var index = Math.Clamp(_paymentList.SelectedItem ?? 0, 0, _paymentLineOwners.Count - 1);
        var owner = _paymentLineOwners[index];
        return _paymentRows.FirstOrDefault(payment => payment.Sequence == owner)?.TransactionId ?? typed;
    }

    /// <summary>Marks the payment selection deliberate, as a real click or keypress would.</summary>
    internal void SelectPaymentRowForTest(int index)
    {
        _paymentList.SelectedItem = index;
        _paymentSelectionIsDeliberate = true;
    }

    /// <summary>Scrolls the payment list, as a real wheel or arrow key would.</summary>
    internal void ScrollPaymentListForTest(int offsetY) =>
        _paymentList.Viewport = _paymentList.Viewport with
        {
            Location = new Point(_paymentList.Viewport.Location.X, offsetY),
        };

    private static string Hint(string hint) => hint.Length == 0 ? string.Empty : $"○ {hint}";

    /// <summary>
    /// Keeps the most recent operator-facing message on screen until the operator's next
    /// action produces evidence. Without the mark the 1.5 second poll erased every failure
    /// message before it could be read.
    /// </summary>
    private void RenderMessageLine(OperatorPresentationModel model)
    {
        var newestSequence = model.Evidence.Count == 0 ? -1 : model.Evidence[0].Sequence;
        if (_message.Length > 0 && newestSequence <= _messageMark)
        {
            _messageLine.Text = $"✕ {_message}";
            _messageLine.SchemeName = OperatorTheme.DestructiveScheme;
            return;
        }

        _message = string.Empty;
        _messageLine.Text = model.EvidenceStrip;
        _messageLine.SchemeName = OperatorTheme.BaseScheme;
    }

    private void ApplyResponsiveLayout()
    {
        var layout = InteractionPolicies.LayoutFor(Frame.Width, Frame.Height);
        _compactLayout = layout != TerminalLayoutMode.Preferred;
        _navigation.Width = _compactLayout ? RailWidthCompact : RailWidthPreferred;
        _navigation.BorderStyle = _compactLayout ? LineStyle.None : LineStyle.Rounded;
        _content.X = Pos.Right(_navigation);
        foreach (var button in _navigationButtons)
        {
            button.X = _compactLayout ? 0 : 1;
            button.Width = _compactLayout ? Dim.Fill() : Dim.Fill(1);
        }

        ApplyOperationsRows();
        ApplyFaultsLayout();
        UpdateNavigationText();
        if (layout == TerminalLayoutMode.BelowMinimum)
        {
            ShowMessage("Terminal below 80×24 — use keyboard shortcuts; session state is preserved.");
        }
    }

    /// <summary>
    /// Drops the blank spacer rows from the Operations form on short terminals so the
    /// outcome lookup stays on screen at the supported 80x24 minimum.
    /// </summary>
    private void ApplyOperationsRows()
    {
        var submit = _compactLayout ? 6 : 7;
        var burst = _compactLayout ? 7 : 9;
        var outcome = _compactLayout ? 12 : 14;
        _submitButton.Y = submit;
        _resendButton.Y = submit;
        _burstCountLabel.Y = burst;
        _burstCount.Y = burst;
        _concurrencyLabel.Y = burst;
        _burstConcurrency.Y = burst;
        _burstButton.Y = burst + 1;
        _cancelBurstButton.Y = burst + 1;
        _burstStatus.Y = burst + 2;
        _burstProvenStatus.Y = burst + 3;
        _outcomeLabel.Y = outcome;
        _outcomeKey.Y = outcome;
        _queryButton.Y = outcome + 1;
        // The payment list takes whatever is left. It is the last thing to give way, because
        // it is where a submitted payment states its own outcome.
        _paymentList.Y = outcome + 3;
    }

    /// <summary>
    /// Below the preferred width the slider track shortens first and the value column keeps
    /// its place — the printed number, not the bar, is the authoritative reading.
    /// </summary>
    private void ApplyFaultsLayout()
    {
        var track = _compactLayout ? FaultTrackWidthCompact : FaultTrackWidthPreferred;
        _faultTrackWidth = track;
        _faultsContentWidth = Math.Max(
            FaultLabelX + FaultLabelWidth + 12,
            Frame.Width - (_compactLayout ? RailWidthCompact : RailWidthPreferred) - 4);
        foreach (var (range, value) in new (LinearRange<int>, Label)[]
                 {
                     (_errorRateRange, _errorRateValue),
                     (_latencyRange, _latencyValue),
                     (_throttleRange, _throttleValue),
                 })
        {
            range.Width = track;
            value.X = FaultTrackX + track + 2;
        }
    }

    private void UpdateNavigationText()
    {
        var active = _controller.State.ActiveWorkspace;
        for (var index = 0; index < _navigationButtons.Length; index++)
        {
            var marker = (WorkspaceKind)index == active ? "▸" : " ";
            _navigationButtons[index].Text = _compactLayout
                ? (index + 1).ToString(CultureInfo.InvariantCulture)
                : $"{marker}{index + 1} {NavigationLabels[index]}";
        }
    }

    protected override bool OnKeyDown(Key key)
    {
        var workspace = key switch
        {
            var value when value == Key.D1 => WorkspaceKind.Operations,
            var value when value == Key.D2 => WorkspaceKind.Resources,
            var value when value == Key.D3 => WorkspaceKind.Evidence,
            var value when value == Key.D4 => WorkspaceKind.LoadTest,
            var value when value == Key.D5 => WorkspaceKind.Faults,
            _ => (WorkspaceKind?)null,
        };
        if (workspace is not null)
        {
            key.Handled = true;
            ActivateWorkspace(workspace.Value);
            return true;
        }

        // Panic-off is bound window-wide, reachable from every workspace without navigating
        // to Faults first, because the moment it is needed is the moment navigating is
        // hardest. Never confirmed, never gated by the in-flight lock.
        if (key == Key.D0)
        {
            key.Handled = true;
            PanicOff();
            return true;
        }

        return base.OnKeyDown(key);
    }

    internal async Task RequestExitAsync()
    {
        _sessionCancellation.Cancel();
        Task[] active;
        lock (_activeActionsLock)
        {
            active = [.. _activeActions];
        }

        try
        {
            await Task.WhenAll(active);
        }
        catch (Exception ex)
        {
            ShowMessage($"Operation ended during shutdown: {ex.GetBaseException().Message}");
        }

        await _onExitRequested();
    }

    /// <summary>
    /// Tries the OS default browser (works wherever one is actually reachable
    /// -- essentially never in this sandbox) and always copies the resolved
    /// URL to the terminal clipboard besides, so the operator can paste it no
    /// matter what. Copying and the status update are marshalled onto the UI
    /// thread together with the OSC 52 write, since Terminal.Gui's own render
    /// loop writes to the same stdout from that thread.
    /// </summary>
    private void OpenKnownLink(string title, string linkId) =>
        Dispatch(() => OpenKnownLinkAsync(title, linkId));

    private async Task OpenKnownLinkAsync(string title, string linkId)
    {
        var result = await _controller.OpenKnownLinkAsync(linkId, _sessionCancellation.Token);
        if (result.Url is null)
        {
            ShowMessage($"{title} is not available yet — attach or start a topology first.");
            return;
        }

        RunOnUiThread(() =>
        {
            var copy = TerminalClipboard.Copy(
                result.Url,
                TerminalOut,
                Environment.GetEnvironmentVariable("TERM"),
                Environment.GetEnvironmentVariable("TMUX"));
            ShowMessage(copy.Succeeded
                ? $"{title} link copied to your terminal clipboard: {result.Url}"
                : $"{title}: {result.Url} — {copy.Message}");
        });
    }

    internal Task TriggerOpenKnownLinkForTestAsync(string title, string linkId) => OpenKnownLinkAsync(title, linkId);

    private void Dispatch(Func<Task> action)
    {
        var task = action();
        LastDispatchedTask = task;
        lock (_activeActionsLock)
        {
            _activeActions.Add(task);
        }

        _ = task.ContinueWith(
            task =>
            {
                lock (_activeActionsLock)
                {
                    _activeActions.Remove(task);
                }

                if (task.Exception is not null)
                {
                    ShowMessage($"Operation failed — {task.Exception.GetBaseException().Message}");
                }
            },
            TaskScheduler.Default);
    }

    private async Task SurfaceAsync(Task<CommandResult> task)
    {
        var result = await task;
        if (!result.Succeeded)
        {
            ShowMessage(result.Message);
        }
    }

    private async Task SurfaceAsync(Task<PaymentResult> task)
    {
        var result = await task;
        if (result.Outcome is PaymentOutcome.Rejected or PaymentOutcome.TransportFailure or PaymentOutcome.Ambiguous)
        {
            ShowMessage(result.ErrorSummary ?? result.Outcome.ToString());
        }
    }

    private async Task SurfaceAsync(Task<InspectionResult> task)
    {
        var result = await task;
        if (!result.Succeeded)
        {
            ShowMessage(result.ErrorSummary ?? $"Inspection failed: {result.Target}");
        }
    }

    private async Task SurfaceAsync(Task<LoadWorkflowResult> task)
    {
        var result = await task;
        if (!result.AllPassed)
        {
            ShowMessage(result.ErrorSummary ?? $"Load workflow failed at {result.FinalPhase}.");
        }
    }

    private async Task SurfaceAsync(Task<EvidenceExportResult> task)
    {
        var result = await task;
        if (!result.Succeeded)
        {
            ShowMessage(result.ErrorSummary ?? "Evidence export failed.");
        }
    }

    /// <summary>
    /// Where OSC 52 is written. Defaults to the console's own output, which is
    /// the terminal Terminal.Gui is drawing to; tests substitute a writer.
    /// </summary>
    internal TextWriter TerminalOut { get; set; } = Console.Out;

    /// <summary>
    /// Copies the evidence detail through the terminal rather than through an
    /// OS clipboard helper -- see <see cref="TerminalClipboard"/> for why the
    /// built-in copy silently did nothing. The outcome always reaches the
    /// status bar: a copy that quietly fails is worse than one that reports it.
    /// </summary>
    private void CopyDetailToTerminalClipboard()
    {
        // Guard on the evidence list, not on the detail text: with nothing
        // selected the pane still holds a placeholder, and copying that would
        // report a cheerful success for a clipboard full of nothing.
        if (_evidenceRows.Count == 0)
        {
            ShowMessage("No action has been recorded this session yet — there is nothing to copy.");
            return;
        }

        var detail = _evidenceDetail.Text;
        if (string.IsNullOrWhiteSpace(detail))
        {
            ShowMessage("Select an entry and press Details first — there is nothing to copy.");
            return;
        }

        ShowMessage(TerminalClipboard.Copy(
            detail,
            TerminalOut,
            Environment.GetEnvironmentVariable("TERM"),
            Environment.GetEnvironmentVariable("TMUX")).Message);
    }

    /// <summary>
    /// Surfaces the outcome of a Ctrl+C copy made through the Terminal.Gui
    /// clipboard (<see cref="Osc52Clipboard"/>), which is otherwise silent.
    /// </summary>
    internal void ShowClipboardResult(ClipboardCopyResult result) => ShowMessage(result.Message);

    private void ShowMessage(string message)
    {
        LastUiMessage = message;
        _message = message;
        _messageMark = _controller.State.Evidence.Count == 0
            ? -1
            : _controller.State.Evidence[^1].Sequence;
        RunOnUiThread(() =>
        {
            _messageLine.Text = $"✕ {message}";
            _messageLine.SchemeName = OperatorTheme.DestructiveScheme;
        });
    }

    private static string ExactCommands(IReadOnlyList<string> instances, string command) =>
        string.Join(
            " && ",
            instances.Select(instance => $"aspire resource {instance} {command.ToLowerInvariant()}"));

    private bool ConfirmAndRestore(ConfirmationRequest request, View trigger)
    {
        var confirmed = _confirmation.Confirm(request);
        trigger.SetFocus();
        return confirmed;
    }

    private void TriggerSelectedResourceAction()
    {
        if (_resourceRows.Count == 0)
        {
            ShowMessage("Select a verified resource before running a resource action.");
            return;
        }

        var index = Math.Clamp(_resourceList.SelectedItem ?? 0, 0, _resourceRows.Count - 1);
        var row = _resourceRows[index];
        if (!Enum.TryParse<ResourceCommand>(row.NextAction, out var command))
        {
            ShowMessage($"{row.Name} has no legal next action in the fresh Aspire state.");
            return;
        }

        var exactCommands = ExactCommands(row.Instances, row.NextAction);
        if (ConfirmAndRestore(
                new ConfirmationRequest($"{row.NextAction} {row.Name}", exactCommands, row.Instances),
                _resourceActionButton))
        {
            Dispatch(() => SurfaceAsync(_controller.ExecuteResourceCommandAsync(row.Name, command, _sessionCancellation.Token)));
        }
    }

    internal string LastUiMessage { get; private set; } = string.Empty;
    internal Task? LastDispatchedTask { get; private set; }
    internal WorkspaceKind VisibleWorkspace => _controller.State.ActiveWorkspace;
    internal int NavigationFrameWidth => _navigation.Frame.Width;
    internal string StatusLineText => _statusLine.Text;
    internal string MessageLineText => _messageLine.Text;
    internal string ResourcesHintText => _resourcesHint.Text;
    internal string LoadHintText => _loadHint.Text;
    internal bool IsWorkspaceVisible(WorkspaceKind workspace) => workspace switch
    {
        WorkspaceKind.Operations => _operationsView.Visible,
        WorkspaceKind.Resources => _resourcesView.Visible,
        WorkspaceKind.Evidence => _evidenceView.Visible,
        WorkspaceKind.LoadTest => _loadView.Visible,
        WorkspaceKind.Faults => _faultsView.Visible,
        _ => false,
    };
    internal bool LoadRunEnabled => _runLoadButton.Enabled;
    internal TextField AmountField => _amount;
    internal TextField FromAccountField => _fromAccount;
    internal TextField ToAccountField => _toAccount;
    internal TextField CurrencyField => _currency;
    internal TextField BurstCountField => _burstCount;
    internal TextField BurstConcurrencyField => _burstConcurrency;
    internal TextField ExpectedUniqueField => _expectedUnique;
    internal Button SubmitButton => _submitButton;
    internal Button RailButton => _railButton;
    internal Button IdempotencyButton => _idempotencyButton;
    internal Button WrapButton => _wrapButton;
    internal Button DetailsButton => _detailsButton;

    internal Button CopyButton => _copyButton;
    internal Button JaegerButton => _jaegerButton;
    internal Button AspireDashboardButton => _aspireDashboardButton;

    internal TextField SuppliedKeyField => _suppliedKey;
    internal Button RefreshButton => _refreshButton;
    internal int MountedWorkspaceCount => _content.SubViews.Count;
    internal Button BurstButton => _burstButton;
    internal Button StopButton => _stopButton;
    internal Button ResourceActionButton => _resourceActionButton;
    internal Button RestartResourceButton => _restartResourceButton;
    internal Button RunLoadButton => _runLoadButton;
    internal bool StartRegularEnabled => _startRegularButton.Enabled;
    internal bool StartLoadTestsEnabled => _startLoadButton.Enabled;
    internal ListView ResourceList => _resourceList;
    internal void SelectResourceForTest(string resourceName)
    {
        var index = _resourceRows.ToList().FindIndex(row => row.Name == resourceName);
        _resourceList.SelectedItem = Math.Max(0, index);
    }
    internal Task TriggerSubmitForTestAsync() => SubmitPaymentAsync();
    internal Task TriggerBurstForTestAsync() => RunBurstAsync();
    internal Task TriggerResendForTestAsync() => SurfaceAsync(_controller.ResendLastPaymentAsync(_sessionCancellation.Token));
    internal Task TriggerQueryForTestAsync() => SurfaceAsync(
        _controller.QueryOutcomeAsync(OutcomeQueryTarget(), _sessionCancellation.Token));
    internal Task TriggerLoadForTestAsync(int expectedUnique) => SurfaceAsync(
        _controller.RunLoadTestAsync(expectedUnique, _sessionCancellation.Token));
    internal Task TriggerExportForTestAsync() => SurfaceAsync(_controller.ExportEvidenceAsync(_sessionCancellation.Token));
    internal Task TriggerInspectForTestAsync(string endpoint) => SurfaceAsync(
        _controller.InspectAsync(endpoint, _sessionCancellation.Token));
    internal void TriggerResourceActionForTest() => TriggerSelectedResourceAction();
    internal void RenderForTest() => Render(PresentationModelBuilder.Build(_controller.State, _time.GetUtcNow()));
    internal ListView PaymentList => _paymentList;
    internal ListView EvidenceList => _evidenceList;
    internal int EvidenceRowCount => _evidenceList.Source?.Count ?? 0;
    internal string EvidenceDetailText => _evidenceDetail.Text;
    internal IReadOnlyList<string> PaymentRowTexts =>
        [.. _paymentList.Source?.ToList().Cast<string>() ?? []];
    internal string FeedStatusText => _feedStatus.Text;
    internal string BurstStatusText => _burstStatus.Text;
    internal string BurstProvenStatusText => _burstProvenStatus.Text;
    internal Button ApplyFaultsButton => _applyFaultsButton;
    internal Button PanicOffButton => _panicOffButton;
    internal Button ArmingButton => _armingButton;
    internal string FaultsHintText => _faultsHint.Text;
    internal string PresetLabelText => _presetLabel.Text;
    internal string FaultCostText => _faultCostLabel.Text;
    internal IReadOnlyList<string> FaultValueTexts =>
        [_errorRateValue.Text, _latencyValue.Text, _throttleValue.Text];
    internal IReadOnlyList<bool> FaultKnobsEnabled =>
        [_errorRateRange.Enabled, _latencyRange.Enabled, _throttleRange.Enabled];
    internal int FaultTrackWidth => _faultTrackWidth;
    internal int FaultsContentWidth => _faultsContentWidth;
    internal int FaultValueColumn => FaultTrackX + _faultTrackWidth + 2;
    internal IReadOnlyList<string> VisiblePresetNames =>
        [.. _presetButtons.Where(button => button.Visible).Select(button => button.Text)];
    internal bool SendFaultKnobKeyForTest(int knob, Key key) =>
        new[] { _errorRateRange, _latencyRange, _throttleRange }[knob].NewKeyDownEvent(key);
    internal void TriggerPresetForTest(int index) => Surface(_controller.StageFaults(_presets[index].Levels));
    internal Task TriggerApplyFaultsForTestAsync() => SurfaceAsync(_controller.ApplyFaultsAsync(_sessionCancellation.Token));
    internal void TriggerArmingToggleForTest() => Surface(_controller.SetArming(!_controller.State.FaultArmingRequested));
    internal bool HandleKeyForTest(Key key) => OnKeyDown(key);
    internal void SetIdempotencyModeForTest(IdempotencyMode mode)
    {
        _idempotencyMode = mode;
        _idempotencyButton.Text = $"Idempotency: {_idempotencyMode}";
    }

    internal void ResizeForTest(int width, int height)
    {
        Width = width;
        Height = height;
        SetRelativeLayout(new Size(width, height));
        ApplyResponsiveLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollCancellation.Cancel();
            _pollCancellation.Dispose();
            _sessionCancellation.Cancel();
            _sessionCancellation.Dispose();
            _controller.StateChanged -= OnStateChanged;

            // Remove() transfers lifecycle ownership, so unmounted workspaces are ours to dispose.
            foreach (var workspace in _workspaces)
            {
                if (!ReferenceEquals(workspace, _mountedWorkspace))
                {
                    workspace.Dispose();
                }
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Rebinds a <see cref="ListView"/> only when its rows actually change and restores the
    /// operator's selection. Rebinding on every poll reset the selection to the first row,
    /// so per-resource commands silently targeted the wrong resource.
    /// </summary>
    private sealed class ListBinding(ListView list)
    {
        private string[] _items = [];

        internal void Bind(IReadOnlyList<string> items)
        {
            if (_items.Length == items.Count && _items.SequenceEqual(items, StringComparer.Ordinal))
            {
                return;
            }

            var selected = list.SelectedItem;
            // SetSource resets the viewport to the top. An arriving broadcast outcome rewrites
            // this list, and a list that scrolled itself under a live demonstration is a stage
            // failure regardless of how good the news is -- so the offset is restored too.
            var offset = list.Viewport.Location;
            _items = [.. items];
            list.SetSource(new ObservableCollection<string>(_items));
            if (_items.Length > 0)
            {
                list.SelectedItem = Math.Clamp(selected ?? 0, 0, _items.Length - 1);
                // Clamped to the last scrollable row, not the last item: a list that shrank
                // would otherwise keep an offset past its own content and render blank.
                var lastOffset = Math.Max(0, _items.Length - Math.Max(1, list.Viewport.Height));
                list.Viewport = list.Viewport with
                {
                    Location = new Point(offset.X, Math.Clamp(offset.Y, 0, lastOffset)),
                };
            }
        }
    }
}
#pragma warning restore CS0618
