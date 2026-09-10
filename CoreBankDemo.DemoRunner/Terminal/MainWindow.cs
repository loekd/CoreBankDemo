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
    /// <summary>
    /// The navigation rail is the longest of the five labels plus its border and not one more:
    /// <c>Load Test</c> with its active marker and one-key shortcut needs 14 cells, the divider
    /// and margin 2. Padding in a persistent rail is paid for by every workspace at once, so the
    /// six cells this gave back are six more for a payment's account identifiers
    /// (DESIGN.md, Layout &amp; Spacing).
    /// </summary>
    private const int RailWidthPreferred = 16;
    private const int RailWidthCompact = 5;
    private const int ActionColumnWidth = 22;

    // Operations workspace column grid, sized so both columns still fit the
    // narrowest supported content area (80 columns minus the compact rail).
    private const int LabelX = 1;
    private const int NarrowFieldWidth = 10;

    // The compose bar's own grid. Two eighteen-character IBANs and their captions consume most
    // of the content line on their own, so the amount, the two chips and the second action
    // cannot share their line -- and the captions are what stay, because an unlabelled IBAN read
    // from the back of a room is a run of digits.
    private const int AccountCaptionWidth = 5;
    private const int AccountFieldWidth = 19;
    private const int AmountCaptionWidth = 6;
    private const int AmountFieldWidth = 8;
    private const int ChipWidth = 19;
    private const int RailChipX = 17;
    private const int KeyChipX = 37;

    /// <summary>
    /// The narrowest content line on which the second compose line's chips and <c>Burst…</c>
    /// still fit together. Below it the action wraps to a third line rather than shedding a
    /// caption or hiding a control: no control in use is ever hidden at any width.
    /// </summary>
    private const int SecondComposeLineMinimumWidth = KeyChipX + ChipWidth + 1 + CardActionSlotWidth;

    /// <summary>
    /// Rows between the terminal's own height and a workspace's inner area: the window border
    /// (2), the topology bar (1), the content frame's border (2) and the workspace frame's
    /// border (2). The shell no longer carries a bottom band at all, which is where the three
    /// rows it used to reserve went (DESIGN.md, Evidence strip — removed). Derived from
    /// <c>Frame.Height</c> rather than read from <c>Viewport</c>, which is not yet recomputed
    /// when the responsive pass runs.
    /// </summary>
    private const int OperationsChromeRows = 7;

    /// <summary>The compose bar's two captioned lines, before any mode-specific third one.</summary>
    private const int ComposeBarRows = 2;

    /// <summary>
    /// Sized to the longest label the slot will ever hold (<c>[ Look up outcome ]</c>), so
    /// <c>[ Cancel payment ]</c> and <c>[ Resend same key ]</c> land where the eye already is.
    /// </summary>
    private const int CardActionSlotWidth = 20;

    /// <summary>Right-aligned, fixed, so the clock does not shuffle sideways as the state resolves.</summary>
    private const int ClockColumnWidth = 8;

    /// <summary>
    /// The rows the card never gives up: its state line, the blank beneath it, the request
    /// detail, both accounts and the meta line. Everything else in the workspace is compressed
    /// into what is left, because the card is where a payment states its own outcome.
    /// </summary>
    private const int CardMinimumRows = 5;

    /// <summary>
    /// The strip never falls below two rows while more than one payment is open: the outage
    /// climax has a standard and an instant payment in flight at once, and a strip that could
    /// not show both would make the operator choose which half of their own sentence to display.
    /// </summary>
    private const int StillOpenFloorRows = 2;

    /// <summary>The transient announcement's row, and the feed status beneath it.</summary>
    private const int OperationsBottomRows = 2;

    // Faults workspace grid. The value column is never sacrificed to preserve the track:
    // the number is authoritative and the bar is reinforcement, so degradation drops the
    // bar first (see ApplyFaultsLayout) and the number never.
    private const int FaultLabelX = 1;
    private const int FaultLabelWidth = 15;
    private const int FaultTrackX = 17;
    private const int FaultTrackWidthPreferred = 24;
    private const int FaultTrackWidthCompact = 10;
    private const int MaximumFaultPresets = 3;

    /// <summary>
    /// The rail's own short labels, deliberately one word each. The rail is
    /// <see cref="RailWidthPreferred"/> cells wide and its longest row is the marker, the
    /// shortcut digit and the label together; a two-word label broke mid-phrase there
    /// ("4 Load" / "Test"), which reads as two workspaces rather than one. The descriptive
    /// names live in <c>PresentationModelBuilder.NavigationLabel</c>, which is where a
    /// longer form belongs.
    /// </summary>
    private static readonly string[] NavigationLabels = ["Operations", "Resources", "Evidence", "Tests", "Faults"];

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
    // Dim.Fill() rather than Dim.Fill(3): the removed bottom band is where those three rows of
    // permanent chrome went, in all five workspaces (EXPERIENCE.md, Information Architecture).
    private readonly FrameView _navigation = new() { X = 0, Y = 1, Width = RailWidthPreferred, Height = Dim.Fill(), Title = "WORKSPACES" };
    private readonly FrameView _content = new() { X = RailWidthPreferred, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };

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
    // Unique per console session on purpose. An idempotency key is a permanent
    // identity: a hard-coded default meant every "Supplied" submit in a new
    // session silently replayed whatever row a previous session had created
    // under that key, so the operator saw hours-old state and no fresh payment.
    // Typing a fixed key to demonstrate a replay is still one keystroke away.
    private readonly TextField _suppliedKey = new() { Text = NewSessionKey() };
    private readonly TextField _burstCount = new() { Text = "20" };
    private readonly TextField _burstConcurrency = new() { Text = "4" };
    private readonly Button _railButton = NewButton("Rail ‹ standard ›");
    private readonly Button _idempotencyButton = NewButton("Key ‹ Generated ›");
    private readonly Button _submitButton = NewButton("Submit", isDefault: true);
    private readonly Button _burstButton = NewButton("Burst…");
    private readonly Button _startBurstButton = NewButton("Start burst");
    private readonly Button _cancelBurstButton = NewButton("Stop sending");

    // --- Operations: the two regions that replace each other -----------------------------
    // A running burst *replaces* the compose bar, the focus card and the strip rather than
    // rendering as one more object among them: during a burst the counters are the
    // demonstration (EXPERIENCE.md, Burst takeover).
    private View _operationsMain = null!;
    private View _burstTakeover = null!;

    // --- Operations: focus card ----------------------------------------------------------
    private readonly Label _composeRule = new();
    private readonly Label _cardState = new();
    private readonly Label _cardClock = new();
    private readonly Button _cardActionButton = NewButton(CardActions.Cancel);
    private readonly Label _cardRequest = new();
    private readonly Label _cardAccounts = new();
    private readonly Label _cardMeta = new();
    private readonly Label _cardClosing = new();

    // --- Operations: STILL OPEN strip ----------------------------------------------------
    // Bound through the shared ListBinding, which preserves both the selection and the scroll
    // offset, so an arriving outcome never moves the strip under an operator who may be
    // mid-sentence with a finger on a line.
    private readonly Label _stillOpenRule = new();
    private readonly ListView _stillOpenList = new();
    private readonly Label _feedStatus = new();

    /// <summary>
    /// The transient announcement's row, one per surface that can be the content area: all five
    /// workspaces plus the burst takeover, which replaces Operations' own. They carry the same
    /// line, because it is one announcement and not six -- a refusal appears at the foot of
    /// whichever workspace is active, where the operator is already looking. None of them
    /// reserves a row: each is hidden outright while there is nothing to say, and the row it
    /// would take returns to the surface behind it.
    /// </summary>
    private readonly List<Label> _announcementRows = [];

    /// <summary>
    /// Rows an announcement borrows its slot from while it is showing. A workspace hint restates
    /// what the controls already say; the announcement is what the operator needs right now.
    /// </summary>
    private readonly List<View> _announcementYields = [];
    private Label _modeLine = null!;
    private Label _burstSetupLabel = null!;
    private Label _burstConcurrencyLabel = null!;

    // --- Operations: burst takeover ------------------------------------------------------
    private readonly Label _burstRule = new();
    private readonly Label _burstStatus = new();
    private readonly Label _burstProvenStatus = new();
    private readonly Label _burstClosing = new();
    private readonly Button _burstDismissButton = NewButton("Done");

    private readonly Label _topologyStatus = new();
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
    private readonly ListBinding _stillOpenBinding;

    private PaymentRail _rail = PaymentRail.Standard;
    private IdempotencyMode _idempotencyMode = IdempotencyMode.Generated;
    private IReadOnlyList<ResourceRowViewModel> _resourceRows = [];
    private IReadOnlyList<EvidenceRowViewModel> _evidenceRows = [];
    private IReadOnlyList<StillOpenRowViewModel> _stillOpenRows = [];
    private FocusCardViewModel _focusCard = FocusCardViewModel.Placeholder;
    private bool _showStillOpen;
    private int _stillOpenVisibleRows;
    private bool _rebindingStillOpenList;
    private bool _rebindingEvidenceList;
    private bool _compactLayout;
    private bool _burstSetupVisible;

    /// <summary>
    /// True while a burst is running or still holding its final summary. A result that clears
    /// itself on completion is a result the room never got to read, so the takeover holds until
    /// the operator dismisses it.
    /// </summary>
    private bool _burstTakeoverActive;
    private string _message = string.Empty;
    private bool _messageIsFailure = true;
    private long _messageMark = -1;

    private readonly UiRepaintCoalescer _repaints = new(AppTerminal.Invoke);

    public MainWindow(OperatorConsoleController controller, Func<Task> onExitRequested, ThemeMode theme = ThemeMode.Dark)
        : this(controller, onExitRequested, null, true, theme: theme)
    {
    }

    internal MainWindow(
        OperatorConsoleController controller,
        Func<Task> onExitRequested,
        IConfirmationService? confirmation,
        bool startPolling,
        bool marshalUpdates = true,
        TimeProvider? time = null,
        ThemeMode theme = ThemeMode.Dark)
    {
        OperatorTheme.Register(theme);
        _controller = controller;
        _time = time ?? TimeProvider.System;
        _onExitRequested = onExitRequested;
        _confirmation = confirmation ?? new TerminalConfirmationService();
        _marshalUpdates = marshalUpdates;
        _resourceBinding = new ListBinding(_resourceList);
        _evidenceBinding = new ListBinding(_evidenceList);
        _loadResultBinding = new ListBinding(_loadResults);
        _stillOpenBinding = new ListBinding(_stillOpenList);
        Title = "CoreBankDemo — Operator Console";
        OperatorTheme.Apply(this, OperatorTheme.BaseScheme);
        OperatorTheme.Apply(_navigation, OperatorTheme.RailScheme);
        // Submit is Operations' single filled-teal control. Burst…, the card's own action and the
        // takeover's dismiss take the object-anchored treatment instead -- and Cancel payment
        // pointedly takes neither the destructive tokens (it destroys nothing) nor the
        // lock-exempt outline (it is exempt from confirmation, never from the lock).
        OperatorTheme.Apply(_submitButton, OperatorTheme.ActionScheme);
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

        Add(_topologyBar, _aspireDashboardButton, _jaegerButton, _navigation, _content);
        UpdateNavigationText();
        FrameChanged += (_, _) => ApplyResponsiveLayout();
        _controller.StateChanged += OnStateChanged;
        Render(PresentationModelBuilder.Build(_controller.State, _time.GetUtcNow()));
        if (startPolling)
        {
            _ = PollAsync(_pollCancellation.Token);
        }
    }

    /// <summary>
    /// The Stage-focus Operations workspace: a two-line compose bar, one large focus card
    /// carrying the selected payment's own action, and a STILL OPEN strip that renders only
    /// while more than one payment is open. Its actions anchor to the object they operate on
    /// rather than to a shared lower region, which this workspace no longer has: Submit and
    /// Burst… belong to the compose bar whose contents they act on, and Cancel payment / Look up
    /// outcome / Resend same key to the card, acting on the payment printed above them.
    /// </summary>
    private View BuildOperationsView()
    {
        var view = NewWorkspace("OPERATIONS");
        _operationsMain = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _burstTakeover = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true, Visible = false };
        BuildComposeBar(_operationsMain);
        BuildFocusCard(_operationsMain);
        BuildStillOpenStrip(_operationsMain);
        BuildBurstTakeover(_burstTakeover);
        view.Add(_operationsMain, _burstTakeover);
        return view;
    }

    /// <summary>
    /// Two captioned lines, each ending in a right-anchored action slot, plus a third line only
    /// where a mode needs one. The captions are what stay at every width: an unlabelled IBAN read
    /// from the back of a room is a run of digits. There is no currency field and no currency
    /// validation — the console always sends EUR.
    /// </summary>
    private void BuildComposeBar(View view)
    {
        // Added in the workspace's literal Tab order: compose-bar first line (From, To, Submit),
        // then the second (Amount, rail chip, idempotency chip, Burst…), then the third line
        // where a mode renders one (EXPERIENCE.md, Accessibility Floor).
        AddField(view, "From", _fromAccount, LabelX, 0, AccountCaptionWidth, AccountFieldWidth);
        AddField(
            view,
            "→ To",
            _toAccount,
            LabelX + AccountCaptionWidth + AccountFieldWidth + 2,
            0,
            AccountCaptionWidth,
            AccountFieldWidth);
        view.Add(_submitButton);
        AddField(view, "Amount", _amount, LabelX, 1, AmountCaptionWidth, AmountFieldWidth);
        _railButton.X = RailChipX;
        _railButton.Y = 1;
        _railButton.Width = ChipWidth;
        _idempotencyButton.X = KeyChipX;
        _idempotencyButton.Y = 1;
        _idempotencyButton.Width = ChipWidth;
        _submitButton.X = Pos.AnchorEnd(CardActionSlotWidth);
        _submitButton.Y = 0;
        _burstButton.X = Pos.AnchorEnd(CardActionSlotWidth);
        _burstButton.Y = 1;

        view.Add(_railButton, _idempotencyButton, _burstButton);

        _railButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            _rail = _rail == PaymentRail.Standard ? PaymentRail.Instant : PaymentRail.Standard;
            _railButton.Text = $"Rail ‹ {_rail.ToString().ToLowerInvariant()} ›";
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
            _idempotencyButton.Text = $"Key ‹ {_idempotencyMode} ›";
            // The mode line costs a row only in the mode that needs it, so the whole workspace is
            // redrawn here rather than only the row ladder: the strip's rule and the feed
            // statement move with it and must not be left stating the old layout.
            Repaint();
        };

        // The one mode-specific third line: the supplied-key field in Supplied mode, the
        // not-retry-safe warning in Omitted mode. No control in use is ever hidden.
        _modeLine = new Label { X = LabelX, Y = 2, Height = 1, Width = 14, Text = "Supplied key" };
        _suppliedKey.X = LabelX + 15;
        _suppliedKey.Y = 2;
        _suppliedKey.Height = 1;
        _suppliedKey.Width = AccountFieldWidth + 6;
        view.Add(_modeLine, _suppliedKey);

        // Burst… reveals the burst control rather than firing one: the count is bounded and the
        // operator states it before two hundred payments leave.
        _burstSetupLabel = new Label { X = LabelX, Y = 3, Height = 1, Width = 12, Text = "Burst count" };
        _burstCount.X = LabelX + 13;
        _burstCount.Y = 3;
        _burstCount.Height = 1;
        _burstCount.Width = NarrowFieldWidth;
        _burstConcurrencyLabel = new Label { X = LabelX + 25, Y = 3, Height = 1, Width = 9, Text = "at once" };
        _burstConcurrency.X = LabelX + 35;
        _burstConcurrency.Y = 3;
        _burstConcurrency.Height = 1;
        _burstConcurrency.Width = NarrowFieldWidth;
        _startBurstButton.X = Pos.AnchorEnd(CardActionSlotWidth);
        _startBurstButton.Y = 3;
        view.Add(_burstSetupLabel, _burstCount, _burstConcurrencyLabel, _burstConcurrency, _startBurstButton);

        _submitButton.Accepting += (_, e) => { e.Handled = true; Dispatch(SubmitPaymentAsync); };
        _burstButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            _burstSetupVisible = !_burstSetupVisible;
            Repaint();
        };
        _startBurstButton.Accepting += (_, e) => { e.Handled = true; Dispatch(RunBurstAsync); };

        _composeRule.X = LabelX;
        _composeRule.Y = ComposeBarRows;
        _composeRule.Height = 1;
        _composeRule.Width = Dim.Fill(1);
        view.Add(_composeRule);
    }

    /// <summary>
    /// The card's first line is a fixed grid — symbol, state word, right-aligned clock, action
    /// slot at the right margin — so nothing on it moves as <c>AWAITING SETTLEMENT</c> resolves
    /// to <c>SETTLED</c>. Beneath it the request detail, both accounts in full, the meta line
    /// with the payment's own submit stamp, and exactly one closing block.
    /// </summary>
    private void BuildFocusCard(View view)
    {
        _cardState.X = LabelX + 2;
        _cardState.Height = 1;
        _cardState.Width = Dim.Fill(CardActionSlotWidth + ClockColumnWidth + 2);
        _cardClock.X = Pos.AnchorEnd(CardActionSlotWidth + ClockColumnWidth + 1);
        _cardClock.Height = 1;
        _cardClock.Width = ClockColumnWidth;
        _cardActionButton.X = Pos.AnchorEnd(CardActionSlotWidth);
        _cardActionButton.Accepting += (_, e) => { e.Handled = true; TriggerCardAction(); };

        foreach (var label in new[] { _cardRequest, _cardAccounts, _cardMeta })
        {
            label.X = LabelX + 6;
            label.Height = 1;
            label.Width = Dim.Fill(1);
        }

        _cardClosing.X = LabelX + 6;
        _cardClosing.Width = Dim.Fill(1);
        view.Add(_cardState, _cardClock, _cardActionButton, _cardRequest, _cardAccounts, _cardMeta, _cardClosing);
    }

    private void BuildStillOpenStrip(View view)
    {
        _stillOpenRule.X = LabelX;
        _stillOpenRule.Height = 1;
        _stillOpenRule.Width = Dim.Fill(1);
        _stillOpenList.X = LabelX;
        _stillOpenList.Width = Dim.Fill(1);
        // Moving selection through the strip re-points the card, and only the operator moves it.
        _stillOpenList.ValueChanged += (_, _) =>
        {
            if (_rebindingStillOpenList || _stillOpenRows.Count == 0)
            {
                return;
            }

            var index = Math.Clamp(_stillOpenList.SelectedItem ?? 0, 0, _stillOpenRows.Count - 1);
            _controller.SelectPayment(_stillOpenRows[index].TransactionId);
        };

        AddAnnouncementRow(view, Pos.AnchorEnd(OperationsBottomRows));
        _feedStatus.X = LabelX;
        _feedStatus.Y = Pos.AnchorEnd(1);
        _feedStatus.Height = 1;
        _feedStatus.Width = Dim.Fill(1);
        view.Add(_stillOpenRule, _stillOpenList, _feedStatus);
    }

    /// <summary>
    /// A running burst occupies the whole content area: its captioned rule names the run and
    /// carries the region's feed statement beside the clock, its body is the two counter lines
    /// and the bar, and its slot holds Stop sending while sending and Done once drained.
    /// </summary>
    private void BuildBurstTakeover(View view)
    {
        _burstRule.X = LabelX;
        _burstRule.Y = 1;
        _burstRule.Height = 1;
        _burstRule.Width = Dim.Fill(1);
        _burstStatus.X = LabelX + 5;
        _burstStatus.Y = 4;
        _burstStatus.Height = 1;
        _burstStatus.Width = Dim.Fill(1);
        _burstProvenStatus.X = LabelX + 5;
        _burstProvenStatus.Y = 6;
        _burstProvenStatus.Height = 1;
        _burstProvenStatus.Width = Dim.Fill(1);
        _burstClosing.X = LabelX + 5;
        _burstClosing.Y = 9;
        _burstClosing.Height = 1;
        _burstClosing.Width = Dim.Fill(1);
        _cancelBurstButton.X = Pos.AnchorEnd(CardActionSlotWidth);
        _cancelBurstButton.Y = Pos.AnchorEnd(1);
        _burstDismissButton.X = Pos.AnchorEnd(CardActionSlotWidth);
        _burstDismissButton.Y = Pos.AnchorEnd(1);
        _cancelBurstButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (!_controller.CancelActiveBurst())
            {
                ShowMessage("No active burst is available to cancel.");
            }
        };
        _burstDismissButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            // The takeover holds its final summary until the operator dismisses it: a result
            // that clears itself on completion is a result the room never got to read.
            _burstTakeoverActive = false;
            Repaint();
        };
        // The takeover replaces every Operations surface, this one included, so it carries its
        // own row rather than letting a refusal vanish behind the counters.
        AddAnnouncementRow(view, Pos.AnchorEnd(2));
        view.Add(_burstRule, _burstStatus, _burstProvenStatus, _burstClosing, _cancelBurstButton, _burstDismissButton);
    }

    /// <summary>
    /// Adds one workspace's announcement row and registers it. <paramref name="yields"/> names a
    /// view that shares the slot and steps aside while the announcement is showing, so no row is
    /// ever held empty against its arrival.
    /// </summary>
    private Label AddAnnouncementRow(View view, Pos y, View? yields = null)
    {
        var row = new Label { X = LabelX, Y = y, Height = 1, Width = Dim.Fill(1), Visible = false };
        view.Add(row);
        _announcementRows.Add(row);
        if (yields is not null)
        {
            _announcementYields.Add(yields);
        }

        return row;
    }

    /// <summary>
    /// Fires whichever of the three labels the card's one action slot is currently carrying. It
    /// acts on the payment the card is showing, so it needs no target control and no
    /// "which one?" step.
    /// </summary>
    private void TriggerCardAction()
    {
        var card = _focusCard;
        switch (card.ActionLabel)
        {
            case CardActions.ResendSameKey:
                Dispatch(() => SurfaceAsync(_controller.ResendLastPaymentAsync(_sessionCancellation.Token)));
                return;

            case CardActions.LookUpOutcome:
                LookUpCardOutcome();
                return;

            default:
                if (card.IsPlaceholder)
                {
                    // Nothing was attempted against a payment, so there is nothing to record.
                    ShowNotice("No payment is on the card yet — submit one first.");
                    return;
                }

                // No confirmation modal: a cancellation destroys no state, and the bank's own
                // answer is what the console will render either way. An Omitted-mode payment with
                // no id goes to the controller too rather than being turned away here, so its
                // refusal is an Evidence record and not an announcement nobody kept.
                Dispatch(() => SurfaceAsync(
                    _controller.CancelPaymentAsync(card.TransactionId ?? string.Empty, _sessionCancellation.Token)));
                return;
        }
    }

    /// <summary>
    /// The deliberate second opinion, on the payment the card already names. Read-only and never
    /// blocked by the single-action-in-flight rule, so it stays reachable from the keyboard even
    /// while the slot is carrying one of the other two labels.
    /// </summary>
    private void LookUpCardOutcome()
    {
        if (_focusCard.IsPlaceholder)
        {
            ShowNotice("No payment is on the card yet — there is nothing to look up.");
            return;
        }

        // Every refusal beyond that one is the controller's to make and to record: a lookup the
        // console turned away in its own hands would leave the announcement as the only copy.
        Dispatch(() => SurfaceAsync(
            _controller.QueryOutcomeAsync(_focusCard.TransactionId ?? string.Empty, _sessionCancellation.Token)));
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

        // The one place the console's own topology status line is read: Resources answers
        // "is it running?", which is the question every sentence on that line is about.
        _topologyStatus.X = 1;
        _topologyStatus.Y = Pos.AnchorEnd(2);
        _topologyStatus.Height = 1;
        _topologyStatus.Width = Dim.Fill(1);
        LayoutHint(_resourcesHint);
        view.Add(_resourceList, actions, _topologyStatus, _resourcesHint);
        AddAnnouncementRow(view, Pos.AnchorEnd(1), _resourcesHint);
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
        // Above the two action rows rather than beside them, and the lists give up the row only
        // while it is showing -- nothing is held empty against its arrival.
        AddAnnouncementRow(view, Pos.AnchorEnd(3));
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
        AddAnnouncementRow(view, Pos.AnchorEnd(1), _loadHint);
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
        AddAnnouncementRow(view, Pos.AnchorEnd(1), _faultsHint);
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
            // Currency is not an operator input: there is no field and no validation rule, and
            // the console always sends EUR (EXPERIENCE.md, Compose bar).
            SettlementCurrency,
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
            // Currency is not an operator input: there is no field and no validation rule, and
            // the console always sends EUR (EXPERIENCE.md, Compose bar).
            SettlementCurrency,
            _rail);
        await SurfaceAsync(_controller.RunBurstAsync(request, count, concurrency, _sessionCancellation.Token));
    }

    /// <summary>
    /// Terminal.Gui raises <c>Accepting</c> for a mouse click on any button but raises
    /// <c>Accepted</c> only for the default button, so every command here handles
    /// <c>Accepting</c> and marks it handled to stop the command bubbling to the default.
    /// </summary>
    /// <summary>
    /// A rail item is bare text, not a bracketed button. Terminal.Gui brackets and pads a
    /// Button's title by default — <c>[ ▸1 Operations ]</c> — four cells the rail's
    /// <see cref="RailWidthPreferred"/> derivation never budgeted for, so every label
    /// overflowed its row and reflowed into the blank row beneath it. The marker is what
    /// carries the active workspace (DESIGN.md, Nav rail item), so the brackets were never
    /// doing any work here. <see cref="View.Height"/> is pinned to one row as well: a label
    /// that outgrows the rail must truncate visibly rather than silently wrap into its
    /// neighbour's row.
    /// </summary>
    private Button CreateNavigationButton(WorkspaceKind workspace, int y)
    {
        var button = NewButton(string.Empty);
        button.NoDecorations = true;
        button.NoPadding = true;
        button.X = 0;
        button.Y = y;
        button.Height = 1;
        button.Width = Dim.Fill();
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
    /// <summary>
    /// Every seeded demo account is denominated in EUR, which is the only thing that makes a
    /// fixed currency safe to send. A caption beside a value the operator cannot change is chrome
    /// that teaches the room nothing.
    /// </summary>
    private const string SettlementCurrency = "EUR";

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

    /// <summary>
    /// Repaints for a state change, coalescing bursts of them into a single pending render.
    /// </summary>
    /// <remarks>
    /// Measured before this: a workspace switch did not reach the screen within twenty-five
    /// seconds under a post-burst event rate, which is what the freeze reports were.
    /// See <see cref="UiRepaintCoalescer"/> for why the requests are folded together.
    /// </remarks>
    private void OnStateChanged(OperatorConsoleState state)
    {
        if (!_marshalUpdates)
        {
            Render(PresentationModelBuilder.Build(state, _time.GetUtcNow()));
            return;
        }

        _repaints.Request(() =>
            Render(PresentationModelBuilder.Build(_controller.State, _time.GetUtcNow())));
    }

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

        RenderOperations(model);
        _loadStatus.Text = model.LoadPhaseStatus;
        _loadResultBinding.Bind(model.LoadResults);

        _topologyStatus.Text = model.TopologyStatus;
        _resourcesHint.Text = Hint(model.ResourcesHint);
        _loadHint.Text = Hint(model.LoadHint);
        RenderFaults(model.Faults);
        _armingButton.Text = model.ArmingCaption;
        _armingButton.Enabled = model.CanChangeArming;

        _submitButton.Enabled = !model.IsBusy;
        _burstButton.Enabled = !model.IsBusy;
        _startBurstButton.Enabled = !model.IsBusy;
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
    /// Draws the three Operations regions, or the burst takeover that replaces all of them.
    /// </summary>
    private void RenderOperations(OperatorPresentationModel model)
    {
        _focusCard = model.FocusCard;
        _stillOpenRows = model.StillOpen;
        _showStillOpen = model.ShowStillOpen;

        // A burst takes the workspace over while it runs, and holds its result until dismissed.
        _burstTakeoverActive = _burstTakeoverActive || model.CanCancelBurst;
        _operationsMain.Visible = !_burstTakeoverActive;
        _burstTakeover.Visible = _burstTakeoverActive;

        _cardState.Text = $"{model.FocusCard.Symbol}  {model.FocusCard.StateWord}";
        _cardClock.Text = model.FocusCard.Clock;
        _cardActionButton.Text = model.FocusCard.ActionLabel;
        _cardActionButton.Enabled = model.FocusCard.ActionEnabled;
        _cardRequest.Text = model.FocusCard.RequestDetail;
        _cardAccounts.Text = model.FocusCard.Accounts;
        _cardMeta.Text = model.FocusCard.Meta;
        var closing = model.FocusCard.Closing.ToList();
        if (!model.FocusCard.ActionEnabled && model.FocusCard.ActionReason.Length > 0)
        {
            // A disabled action always states its reason on the card rather than implying it.
            closing.Add(model.FocusCard.ActionReason);
        }

        _cardClosing.Text = string.Join(Environment.NewLine, closing);

        _rebindingStillOpenList = true;
        try
        {
            _stillOpenBinding.Bind([.. model.StillOpen.Select(row => row.Line)]);

            // The list's own cursor follows the card, so the ▸ marker and the highlight can never
            // disagree about which payment is selected -- an operator who arrows off a line the
            // cursor was never on would jump somewhere they did not choose.
            var selected = model.StillOpen.ToList().FindIndex(row => row.Selected);
            if (selected >= 0 && _stillOpenList.SelectedItem != selected)
            {
                _stillOpenList.SelectedItem = selected;
            }
        }
        finally
        {
            _rebindingStillOpenList = false;
        }

        ApplyOperationsRows();

        // The feed statement is made once for the region: on the strip's own captioned rule when
        // the strip is showing, right-aligned at the foot of the card region when it is not, and
        // on the takeover's rule while a burst holds the workspace. It is never blanked.
        var surplus = model.StillOpen.Count - _stillOpenVisibleRows;
        _stillOpenRule.Text = RuleText(
            surplus > 0 ? $"STILL OPEN · +{surplus} more open" : "STILL OPEN",
            model.FeedStatus,
            RuleWidth());
        _feedStatus.Text = model.FeedStatus;
        _feedStatus.Visible = !_showStillOpen;
        _composeRule.Text = new string('─', Math.Max(1, RuleWidth()));

        _burstRule.Text = RuleText(model.BurstCaption, model.FeedStatus, RuleWidth());
        _burstStatus.Text = model.BurstStatus;
        _burstProvenStatus.Text = model.BurstProvenStatus;
        _burstClosing.Text = model.BurstClosing;
        RenderAnnouncement(model);
        _cancelBurstButton.Visible = model.CanCancelBurst;
        _burstDismissButton.Visible = !model.CanCancelBurst;
    }

    /// <summary>The workspace's usable inner line: 80 cells at 100 columns, 71 at the floor.</summary>
    private int ContentWidth() =>
        Math.Max(1, Frame.Width - (_compactLayout ? RailWidthCompact : RailWidthPreferred) - 4);

    private int RuleWidth() => Math.Max(1, ContentWidth() - 2);

    /// <summary>
    /// A captioned hairline: the caption interrupts its left end and a meta qualifier its right.
    /// The qualifier is surrendered at no width — dropping it would leave a blank where the
    /// console's most important sentence used to be, which reads as nothing being wrong.
    /// </summary>
    private static string RuleText(string caption, string qualifier, int width)
    {
        var head = $"{caption} ";
        var tail = $" {qualifier}";
        var fill = width - head.Length - tail.Length;
        return fill > 0 ? head + new string('─', fill) + tail : $"{caption} · {qualifier}";
    }

    /// <summary>
    /// Moves the strip's selection exactly as an arrow key or a click would, and no further: the
    /// production <c>ValueChanged</c> handler is what re-points the card, so a seam that called
    /// the controller itself would leave a read-only strip on stage and a green test suite.
    /// </summary>
    internal void SelectStillOpenRowForTest(int index) => _stillOpenList.SelectedItem = index;

    /// <summary>Scrolls the strip, as a real wheel or arrow key would.</summary>
    internal void ScrollStillOpenListForTest(int offsetY) =>
        _stillOpenList.Viewport = _stillOpenList.Viewport with
        {
            Location = new Point(_stillOpenList.Viewport.Location.X, offsetY),
        };

    private static string Hint(string hint) => hint.Length == 0 ? string.Empty : $"○ {hint}";

    /// <summary>
    /// Draws the transient announcement on every surface that can be the content area. It takes
    /// the tone of the thing it announces and never a fixed one: a refused or failed command is a
    /// proven failure and renders as one (<c>✕</c>, the failure token), while a notice that is no
    /// verdict at all — a lost feed, a palette switch, a copied link — renders as <c>○</c> in the
    /// neutral tone the rows behind it are taking at that instant. An announcement must never
    /// assert a failure the states beneath it are carefully declining to assert.
    /// <para>
    /// It carries no fact that is not already durable: the same line is an Evidence record from
    /// the moment it appears, refusals the console produced itself included. It reserves no row
    /// when there is nothing to say — the row returns to the surface behind it — and the message
    /// survives until the operator's next action produces evidence, because without that mark the
    /// 1.5 second poll erased every failure before it could be read.
    /// </para>
    /// </summary>
    private void RenderAnnouncement(OperatorPresentationModel model)
    {
        var newestSequence = model.Evidence.Count == 0 ? -1 : model.Evidence[0].Sequence;
        string text;
        bool failure;
        if (_message.Length > 0 && newestSequence <= _messageMark)
        {
            failure = _messageIsFailure;
            text = $"{(failure ? "✕" : "○")} {_message}";
        }
        else
        {
            _message = string.Empty;
            failure = model.AnnouncementIsFailure;
            text = model.Announcement.Length == 0
                ? string.Empty
                : $"{(failure ? "✕" : "○")} {model.Announcement}";
        }

        ApplyAnnouncement(text, failure);
    }

    /// <summary>
    /// Puts one announcement on every surface at once and takes its row straight back when it
    /// clears. Shared by the render pass and by <see cref="Announce"/>, so a line raised between
    /// renders lands on exactly the same rows the next render would have given it.
    /// </summary>
    private void ApplyAnnouncement(string text, bool failure)
    {
        var showing = text.Length > 0;
        foreach (var row in _announcementRows)
        {
            row.Text = text;
            row.SchemeName = failure ? OperatorTheme.DestructiveScheme : OperatorTheme.BaseScheme;
            row.Visible = showing;
        }

        foreach (var yielded in _announcementYields)
        {
            yielded.Visible = !showing;
        }

        // Evidence has no spare row at its foot, so its two panes lend one for as long as the
        // announcement is showing and take it straight back when it clears.
        _evidenceList.Height = Dim.Fill(showing ? 4 : 3);
        _evidenceDetail.Height = Dim.Fill(showing ? 4 : 3);
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
            button.X = 0;
            button.Width = Dim.Fill();
        }

        ApplyOperationsRows();
        ApplyFaultsLayout();
        UpdateNavigationText();
        if (layout == TerminalLayoutMode.BelowMinimum)
        {
            ShowNotice("Terminal below 80×24 — use keyboard shortcuts; session state is preserved.");
        }
    }

    /// <summary>
    /// Places the three Operations regions.
    /// <para>
    /// The focus card is budgeted <b>first</b> and everything else is compressed into what is
    /// left, because the card is where a payment states its own outcome and the room reads it
    /// from the back. Rows are surrendered in a fixed order: the blank rows separating the
    /// regions first; then the STILL OPEN strip, which yields before either of the others and
    /// never falls below two rows while more than one payment is open; then the card's closing
    /// block, which is narration the operator can also simply say aloud. Beyond that the card
    /// yields nothing: its state word, its clock and its action are the last things on this
    /// screen, in that order. Nothing is ever hidden — a surplus the strip cannot show is stated
    /// on its rule as an explicit count.
    /// </para>
    /// </summary>
    private void ApplyOperationsRows()
    {
        var inner = Math.Max(0, Frame.Height - OperationsChromeRows);

        // Where the chips and Burst… cannot share the second line they wrap to a third rather
        // than shedding a caption: the wrap is the only legal answer, because nothing is ever
        // hidden (EXPERIENCE.md, Operations compose bar at the 80x24 floor).
        var wrapped = ContentWidth() < SecondComposeLineMinimumWidth;
        _burstButton.Y = wrapped ? ComposeBarRows : 1;
        var wrapRows = wrapped ? 1 : 0;

        // The mode line speaks only about Supplied and Omitted mode, so in Generated mode it is a
        // row of noise and the first one reclaimed.
        var supplied = _idempotencyMode == IdempotencyMode.Supplied;
        var omitted = _idempotencyMode == IdempotencyMode.Omitted;
        _modeLine.Visible = supplied || omitted;
        _suppliedKey.Visible = supplied;
        _modeLine.Text = supplied
            ? "Supplied key"
            : "Omitted mode: not retry-safe after an ambiguous outcome";
        _modeLine.Width = supplied ? 14 : Dim.Fill(1);

        _burstSetupLabel.Visible = _burstSetupVisible;
        _burstCount.Visible = _burstSetupVisible;
        _burstConcurrencyLabel.Visible = _burstSetupVisible;
        _burstConcurrency.Visible = _burstSetupVisible;
        _startBurstButton.Visible = _burstSetupVisible;

        var modeRow = ComposeBarRows + wrapRows;
        var burstRow = modeRow + (_modeLine.Visible ? 1 : 0);
        var ruleRow = burstRow + (_burstSetupVisible ? 1 : 0);
        _modeLine.Y = modeRow;
        _suppliedKey.Y = modeRow;
        foreach (var view in new View[] { _burstSetupLabel, _burstCount, _burstConcurrencyLabel, _burstConcurrency, _startBurstButton })
        {
            view.Y = burstRow;
        }

        _composeRule.Y = ruleRow;

        // A blank row above the card's first line, so the card looks finished standing alone
        // rather than like the top half of a layout that expects a list beneath it.
        var cardTop = ruleRow + 2;
        _cardState.Y = cardTop;
        _cardClock.Y = cardTop;
        _cardActionButton.Y = cardTop;
        _cardRequest.Y = cardTop + 2;
        _cardAccounts.Y = cardTop + 3;
        _cardMeta.Y = cardTop + 4;
        _cardClosing.Y = cardTop + 6;

        var available = inner - OperationsBottomRows - cardTop - CardMinimumRows;
        var rows = 0;
        if (_showStillOpen && _stillOpenRows.Count > 0)
        {
            // One row per open payment, and the rule above them.
            rows = Math.Clamp(_stillOpenRows.Count, 0, Math.Max(0, available - 1));
            if (rows < StillOpenFloorRows)
            {
                rows = Math.Min(StillOpenFloorRows, Math.Max(0, available - 1));
            }
        }

        _stillOpenVisibleRows = rows;
        var stripShowing = rows > 0;
        _showStillOpen = stripShowing;
        _stillOpenRule.Visible = stripShowing;
        _stillOpenList.Visible = stripShowing;
        _stillOpenRule.Y = Pos.AnchorEnd(OperationsBottomRows + rows + 1);
        _stillOpenList.Y = Pos.AnchorEnd(OperationsBottomRows + rows);
        _stillOpenList.Height = Math.Max(1, rows);

        // The closing block is narration and gives up its rows before the card's own grid does.
        var closingRows = inner - OperationsBottomRows - (stripShowing ? rows + 1 : 0) - (cardTop + 6);
        _cardClosing.Visible = closingRows > 0;
        _cardClosing.Height = Math.Max(1, closingRows);

        // The ladder just moved rows that Pos/Dim resolve against, and this runs outside the
        // draw loop (a state change, a mode chip, a Burst… toggle). Without re-resolving here,
        // every frame on this surface keeps whatever geometry the last resize gave it -- which is
        // exactly how a payment area squeezed to zero rows once passed for correct.
        _operationsMain.SetNeedsLayout();
        if (_operationsMain.SuperView is { } surface && surface.Viewport.Height > 0)
        {
            _operationsMain.Layout(surface.Viewport.Size);
        }
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

        // R and Q were the removed StatusBar's only home. They survive as window-wide keys, so
        // no capability left with the band -- keyboard parity is a floor, not a nicety.
        if (key == Key.R || key == Key.R.WithShift)
        {
            key.Handled = true;
            Dispatch(() => _controller.RefreshAsync(_sessionCancellation.Token));
            return true;
        }

        if (key == Key.Q || key == Key.Q.WithShift)
        {
            key.Handled = true;
            Dispatch(RequestExitAsync);
            return true;
        }

        // The card's outcome lookup: read-only, never blocked by the single-action-in-flight
        // rule, and reachable even while the card's one action slot is carrying Cancel payment
        // or Resend same key.
        if (key == Key.O || key == Key.O.WithShift)
        {
            key.Handled = true;
            LookUpCardOutcome();
            return true;
        }

        // Light/dark toggle. Like 1-5 and 0, this only reaches the window when focus is not
        // in a text field, so typing a 't' into an account or key field is unaffected. The
        // swap remaps the six schemes in place and redraws: no focus move, no scroll reset,
        // no in-flight action disturbed, so it is safe to hit mid-demo when the projector
        // turns out to be washing the dark canvas out.
        if (key == Key.T || key == Key.T.WithShift)
        {
            key.Handled = true;
            var mode = OperatorTheme.Toggle();
            SetNeedsDraw();
            ShowNotice($"Theme: {(mode == ThemeMode.Light ? "light" : "dark")}");
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
            if (copy.Succeeded)
            {
                ShowNotice($"{title} link copied to your terminal clipboard: {result.Url}");
            }
            else
            {
                ShowMessage($"{title}: {result.Url} — {copy.Message}");
            }
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
        else if (result.Outcome == PaymentOutcome.Cancelled)
        {
            // Not an error, but not the 200 the operator was demonstrating either: say what the
            // rail said, in its own terms (ADR-020).
            ShowMessage(
                $"{result.StatusCode} Cancelled — the instant rail timed out and withdrew the payment before it executed. "
                + "Nothing moved; a retry with a new key is safe.");
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

        ShowClipboardResult(TerminalClipboard.Copy(
            detail,
            TerminalOut,
            Environment.GetEnvironmentVariable("TERM"),
            Environment.GetEnvironmentVariable("TMUX")));
    }

    /// <summary>
    /// Surfaces the outcome of a Ctrl+C copy made through the Terminal.Gui
    /// clipboard (<see cref="Osc52Clipboard"/>), which is otherwise silent.
    /// </summary>
    internal void ShowClipboardResult(ClipboardCopyResult result)
    {
        if (result.Succeeded)
        {
            ShowNotice(result.Message);
        }
        else
        {
            ShowMessage(result.Message);
        }
    }

    /// <summary>A command that ran and was refused: a proven failure, and rendered as one.</summary>
    private void ShowMessage(string message) => Announce(message, isFailure: true);

    /// <summary>
    /// A notice that is no verdict at all — a palette switch, a copied link, a size hint. It
    /// takes the neutral tone rather than the failure token, because the largest single-line
    /// statement on the screen must never assert a failure that nothing has proved.
    /// </summary>
    private void ShowNotice(string message) => Announce(message, isFailure: false);

    private void Announce(string message, bool isFailure)
    {
        LastUiMessage = message;
        _message = message;
        _messageIsFailure = isFailure;
        _messageMark = _controller.State.Evidence.Count == 0
            ? -1
            : _controller.State.Evidence[^1].Sequence;
        RunOnUiThread(() => ApplyAnnouncement($"{(isFailure ? "✕" : "○")} {message}", isFailure));
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

    internal IReadOnlyList<Button> NavigationButtons => _navigationButtons;
    internal string AnnouncementLineText => _announcementRows[0].Text;
    internal bool AnnouncementVisibleIn(WorkspaceKind workspace) =>
        _announcementRows.Any(row => row.Visible && IsUnder(row, _workspaces[(int)workspace]));
    internal bool AnnouncementIsFailureToned =>
        _announcementRows[0].SchemeName == OperatorTheme.DestructiveScheme;

    private static bool IsUnder(View view, View ancestor)
    {
        for (var parent = view.SuperView; parent is not null; parent = parent.SuperView)
        {
            if (ReferenceEquals(parent, ancestor))
            {
                return true;
            }
        }

        return false;
    }
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
    internal TextField BurstCountField => _burstCount;
    internal TextField BurstConcurrencyField => _burstConcurrency;
    internal TextField ExpectedUniqueField => _expectedUnique;
    internal Button SubmitButton => _submitButton;
    internal Button CardActionButton => _cardActionButton;
    internal Button BurstDismissButton => _burstDismissButton;
    internal Button CancelBurstButton => _cancelBurstButton;
    internal FocusCardViewModel FocusCard => _focusCard;
    internal bool StillOpenVisible => _stillOpenList.Visible;
    internal int StillOpenVisibleRows => _stillOpenVisibleRows;
    internal string StillOpenRuleText => _stillOpenRule.Text;
    internal bool BurstTakeoverVisible => _burstTakeover.Visible;
    internal string BurstClosingText => _burstClosing.Text;
    internal View CardStateLabel => _cardState;
    internal View CardMetaLabel => _cardMeta;
    internal View CardClosingLabel => _cardClosing;
    internal View StillOpenRuleLabel => _stillOpenRule;
    internal View ComposeRuleLabel => _composeRule;
    internal string TopologyStatusText => _topologyStatus.Text;

    /// <summary>The rows the Operations payment area actually has, chrome removed.</summary>
    internal int OperationsPaymentAreaRows =>
        Math.Max(0, Frame.Height - OperationsChromeRows - ComposeBarRows - 1);

    internal int OperationsChromeRowCount => OperationsChromeRows;
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
    /// <summary>
    /// Goes through the production lookup rather than round the side of it, so the card's own
    /// action and the <c>O</c> key are the paths under test.
    /// </summary>
    internal Task TriggerQueryForTestAsync()
    {
        LookUpCardOutcome();
        return LastDispatchedTask ?? Task.CompletedTask;
    }
    internal void TriggerCardActionForTest() => TriggerCardAction();
    internal Task TriggerLoadForTestAsync(int expectedUnique) => SurfaceAsync(
        _controller.RunLoadTestAsync(expectedUnique, _sessionCancellation.Token));
    internal Task TriggerExportForTestAsync() => SurfaceAsync(_controller.ExportEvidenceAsync(_sessionCancellation.Token));
    internal Task TriggerInspectForTestAsync(string endpoint) => SurfaceAsync(
        _controller.InspectAsync(endpoint, _sessionCancellation.Token));
    internal void TriggerResourceActionForTest() => TriggerSelectedResourceAction();
    private void Repaint() => Render(PresentationModelBuilder.Build(_controller.State, _time.GetUtcNow()));

    internal void RenderForTest() => Repaint();
    internal ListView StillOpenList => _stillOpenList;
    internal ListView EvidenceList => _evidenceList;
    internal int EvidenceRowCount => _evidenceList.Source?.Count ?? 0;
    internal string EvidenceDetailText => _evidenceDetail.Text;
    internal IReadOnlyList<string> StillOpenRowTexts =>
        [.. _stillOpenList.Source?.ToList().Cast<string>() ?? []];
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
    internal bool ModeLineVisible => _modeLine.Visible;
    internal string ModeLineText => _modeLine.Text;

    internal void SetIdempotencyModeForTest(IdempotencyMode mode)
    {
        _idempotencyMode = mode;
        _idempotencyButton.Text = $"Idempotency: {_idempotencyMode}";
        ApplyOperationsRows();
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
