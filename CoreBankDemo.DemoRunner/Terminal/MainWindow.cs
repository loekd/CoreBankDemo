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

    private static readonly string[] NavigationLabels = ["Operations", "Resources", "Evidence", "Load Test"];

    private readonly OperatorConsoleController _controller;
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
    private readonly Button _queryButton = NewButton("Query outcome");
    private readonly Label _burstStatus = new();
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

    private readonly ListBinding _resourceBinding;
    private readonly ListBinding _evidenceBinding;
    private readonly ListBinding _loadResultBinding;

    private PaymentRail _rail = PaymentRail.Standard;
    private IdempotencyMode _idempotencyMode = IdempotencyMode.Generated;
    private IReadOnlyList<ResourceRowViewModel> _resourceRows = [];
    private IReadOnlyList<EvidenceRowViewModel> _evidenceRows = [];
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
        bool marshalUpdates = true)
    {
        OperatorTheme.Register();
        _controller = controller;
        _onExitRequested = onExitRequested;
        _confirmation = confirmation ?? new TerminalConfirmationService();
        _marshalUpdates = marshalUpdates;
        _resourceBinding = new ListBinding(_resourceList);
        _evidenceBinding = new ListBinding(_evidenceList);
        _loadResultBinding = new ListBinding(_loadResults);
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

        _navigationButtons =
        [
            CreateNavigationButton(WorkspaceKind.Operations, 0),
            CreateNavigationButton(WorkspaceKind.Resources, 2),
            CreateNavigationButton(WorkspaceKind.Evidence, 4),
            CreateNavigationButton(WorkspaceKind.LoadTest, 6),
        ];
        _navigation.Add(_navigationButtons);

        _operationsView = BuildOperationsView();
        _resourcesView = BuildResourcesView();
        _evidenceView = BuildEvidenceView();
        _loadView = BuildLoadView();
        _workspaces = [_operationsView, _resourcesView, _evidenceView, _loadView];

        var statusBar = new StatusBar(
        [
            new Shortcut("1", "Operations", () => ActivateWorkspace(WorkspaceKind.Operations)),
            new Shortcut("2", "Resources", () => ActivateWorkspace(WorkspaceKind.Resources)),
            new Shortcut("3", "Evidence", () => ActivateWorkspace(WorkspaceKind.Evidence)),
            new Shortcut("4", "Load Test", () => ActivateWorkspace(WorkspaceKind.LoadTest)),
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
        Render(PresentationModelBuilder.Build(_controller.State));
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

        _outcomeLabel = AddField(view, "Outcome key", _outcomeKey, LabelX, 13, LabelWidth, WideFieldWidth);
        _queryButton.X = LabelX;
        _queryButton.Y = 14;
        _queryButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            Dispatch(() => SurfaceAsync(_controller.QueryOutcomeAsync(_outcomeKey.Text.ToString() ?? string.Empty, _sessionCancellation.Token)));
        };
        LayoutHint(_operationsHint);
        view.Add(_railButton, _idempotencyButton, _submitButton, _resendButton, _burstButton, _cancelBurstButton, _burstStatus, _queryButton, _operationsHint);
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
        RunOnUiThread(() => Render(PresentationModelBuilder.Build(state)));

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
        Render(PresentationModelBuilder.Build(_controller.State));
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
        _evidenceBinding.Bind(model.Evidence.Count == 0
            ? ["○ No actions yet this session"]
            : [.. model.Evidence.Select(row => $"{row.Summary} · {row.Provenance}")]);
        if (!string.Equals(_evidenceDetail.Text, model.SelectedEvidenceDetail, StringComparison.Ordinal))
        {
            _evidenceDetail.Text = model.SelectedEvidenceDetail;
        }

        _statusLine.Text = model.MutationStatus;
        RenderMessageLine(model);
        _burstStatus.Text = model.BurstStatus;
        _loadStatus.Text = model.LoadPhaseStatus;
        _loadResultBinding.Bind(model.LoadResults);

        _operationsHint.Text = Hint(model.OperationsHint);
        _resourcesHint.Text = Hint(model.ResourcesHint);
        _loadHint.Text = Hint(model.LoadHint);

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
        var outcome = _compactLayout ? 10 : 13;
        _submitButton.Y = submit;
        _resendButton.Y = submit;
        _burstCountLabel.Y = burst;
        _burstCount.Y = burst;
        _concurrencyLabel.Y = burst;
        _burstConcurrency.Y = burst;
        _burstButton.Y = burst + 1;
        _cancelBurstButton.Y = burst + 1;
        _burstStatus.Y = burst + 2;
        _outcomeLabel.Y = outcome;
        _outcomeKey.Y = outcome;
        _queryButton.Y = outcome + 1;
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
            _ => (WorkspaceKind?)null,
        };
        if (workspace is not null)
        {
            key.Handled = true;
            ActivateWorkspace(workspace.Value);
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
        _controller.QueryOutcomeAsync(_outcomeKey.Text.ToString() ?? string.Empty, _sessionCancellation.Token));
    internal Task TriggerLoadForTestAsync(int expectedUnique) => SurfaceAsync(
        _controller.RunLoadTestAsync(expectedUnique, _sessionCancellation.Token));
    internal Task TriggerExportForTestAsync() => SurfaceAsync(_controller.ExportEvidenceAsync(_sessionCancellation.Token));
    internal Task TriggerInspectForTestAsync(string endpoint) => SurfaceAsync(
        _controller.InspectAsync(endpoint, _sessionCancellation.Token));
    internal void TriggerResourceActionForTest() => TriggerSelectedResourceAction();
    internal void RenderForTest() => Render(PresentationModelBuilder.Build(_controller.State));
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
            _items = [.. items];
            list.SetSource(new ObservableCollection<string>(_items));
            if (_items.Length > 0)
            {
                list.SelectedItem = Math.Clamp(selected ?? 0, 0, _items.Length - 1);
            }
        }
    }
}
#pragma warning restore CS0618
