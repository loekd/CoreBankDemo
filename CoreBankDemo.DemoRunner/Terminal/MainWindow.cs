using System.Collections.ObjectModel;
using System.Globalization;
using CoreBankDemo.DemoRunner.Application;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using AppTerminal = Terminal.Gui.App.Application;

namespace CoreBankDemo.DemoRunner.Terminal;

#pragma warning disable CS0618
public sealed class MainWindow : Window
{
    private const int CompactWidthThreshold = 100;
    private readonly OperatorConsoleController _controller;
    private readonly Func<Task> _onExitRequested;
    private readonly CancellationTokenSource _pollCancellation = new();
    private readonly CancellationTokenSource _sessionCancellation = new();
    private readonly object _activeActionsLock = new();
    private readonly HashSet<Task> _activeActions = [];

    private readonly Label _topologyBar = new() { X = 1, Y = 0, Height = 1 };
    private readonly Button _aspireDashboardButton = new() { Text = "Aspire" };
    private readonly Button _jaegerButton = new() { Text = "Jaeger" };
    private readonly FrameView _navigation = new() { X = 0, Y = 1, Width = 18, Height = Dim.Fill(3), Title = "WORKSPACES" };
    private readonly FrameView _content = new() { X = 18, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(3) };
    private readonly Label _evidenceStrip = new() { X = 1, Y = Pos.AnchorEnd(2), Height = 1, Width = Dim.Fill(1) };

    private readonly Button[] _navigationButtons;
    private readonly View _operationsView;
    private readonly View _resourcesView;
    private readonly View _evidenceView;
    private readonly View _loadView;

    private readonly TextField _fromAccount = new() { Text = "NL91ABNA0417164300" };
    private readonly TextField _toAccount = new() { Text = "NL20INGB0001234567" };
    private readonly TextField _amount = new() { Text = "1.00" };
    private readonly TextField _currency = new() { Text = "EUR" };
    private readonly TextField _suppliedKey = new() { Text = "demo-key-001" };
    private readonly TextField _outcomeKey = new();
    private readonly TextField _burstCount = new() { Text = "20" };
    private readonly TextField _burstConcurrency = new() { Text = "4" };
    private readonly Button _railButton = new() { Text = "Rail: standard" };
    private readonly Button _idempotencyButton = new() { Text = "Idempotency: Generated" };
    private readonly Button _submitButton = new() { Text = "Submit payment", IsDefault = true };
    private readonly Button _resendButton = new() { Text = "Resend same key" };
    private readonly Button _burstButton = new() { Text = "Run bounded burst" };
    private readonly Button _cancelBurstButton = new() { Text = "Cancel active burst" };
    private readonly Button _queryButton = new() { Text = "Query outcome" };
    private readonly Label _burstStatus = new();

    private readonly ListView _resourceList = new();
    private readonly Button _startRegularButton = new() { Text = "Start Regular" };
    private readonly Button _attachRegularButton = new() { Text = "Attach Regular" };
    private readonly Button _startLoadButton = new() { Text = "Start LoadTests" };
    private readonly Button _attachLoadButton = new() { Text = "Attach LoadTests" };
    private readonly Button _stopButton = new() { Text = "Stop AppHost" };
    private readonly Button _switchButton = new() { Text = "Switch topology" };
    private readonly Button _resourceActionButton = new() { Text = "Resource action" };
    private readonly Button _refreshButton = new() { Text = "Refresh live state" };

    private readonly ListView _evidenceList = new();
    private readonly TextView _evidenceDetail = new() { ReadOnly = true, WordWrap = false };
    private readonly Button _detailsButton = new() { Text = "Details" };
    private readonly Button _wrapButton = new() { Text = "Wrap: off" };
    private readonly Button _exportButton = new() { Text = "Export session evidence" };
    private readonly Button _inspectPaymentsOutbox = new() { Text = "Payments outbox" };
    private readonly Button _inspectCoreBankInbox = new() { Text = "CoreBank inbox" };

    private readonly Label _loadPhase = new();
    private readonly ListView _loadResults = new();
    private readonly Button _runLoadButton = new() { Text = "Run accepted load workflow" };
    private readonly TextField _expectedUnique = new() { Text = "100" };

    private PaymentRail _rail = PaymentRail.Standard;
    private IdempotencyMode _idempotencyMode = IdempotencyMode.Generated;
    private IReadOnlyList<ResourceRowViewModel> _resourceRows = [];
    private IReadOnlyList<EvidenceRowViewModel> _evidenceRows = [];

    public MainWindow(OperatorConsoleController controller, Func<Task> onExitRequested)
    {
        OperatorTheme.Register();
        _controller = controller;
        _onExitRequested = onExitRequested;
        Title = "CoreBankDemo — Operator Console";
        OperatorTheme.Apply(this, OperatorTheme.BaseScheme);
        OperatorTheme.Apply(_navigation, OperatorTheme.RailScheme);
        OperatorTheme.Apply(_submitButton, OperatorTheme.ActionScheme);
        OperatorTheme.Apply(_resendButton, OperatorTheme.ActionScheme);
        OperatorTheme.Apply(_burstButton, OperatorTheme.ActionScheme);
        OperatorTheme.Apply(_queryButton, OperatorTheme.ActionScheme);
        OperatorTheme.Apply(_resourceActionButton, OperatorTheme.DestructiveScheme);
        OperatorTheme.Apply(_stopButton, OperatorTheme.DestructiveScheme);
        OperatorTheme.Apply(_switchButton, OperatorTheme.DestructiveScheme);
        OperatorTheme.Apply(_runLoadButton, OperatorTheme.DestructiveScheme);

        _navigationButtons =
        [
            CreateNavigationButton("1 Operations", WorkspaceKind.Operations, 0),
            CreateNavigationButton("2 Resources", WorkspaceKind.Resources, 2),
            CreateNavigationButton("3 Evidence", WorkspaceKind.Evidence, 4),
            CreateNavigationButton("4 Load Test", WorkspaceKind.LoadTest, 6),
        ];
        _navigation.Add(_navigationButtons);

        _operationsView = BuildOperationsView();
        _resourcesView = BuildResourcesView();
        _evidenceView = BuildEvidenceView();
        _loadView = BuildLoadView();
        _content.Add(_operationsView, _resourcesView, _evidenceView, _loadView);

        var statusBar = new StatusBar(
        [
            new Shortcut("1", "Operations", () => ActivateWorkspace(WorkspaceKind.Operations)),
            new Shortcut("2", "Resources", () => ActivateWorkspace(WorkspaceKind.Resources)),
            new Shortcut("3", "Evidence", () => ActivateWorkspace(WorkspaceKind.Evidence)),
            new Shortcut("4", "Load Test", () => ActivateWorkspace(WorkspaceKind.LoadTest)),
            new Shortcut("R", "Refresh", () => Dispatch(() => _controller.RefreshAsync(_sessionCancellation.Token))),
            new Shortcut("Q", "Quit", () => Dispatch(RequestExitAsync)),
        ]);

        _topologyBar.Width = Dim.Fill(22);
        _aspireDashboardButton.X = Pos.AnchorEnd(20);
        _aspireDashboardButton.Y = 0;
        _jaegerButton.X = Pos.Right(_aspireDashboardButton) + 1;
        _jaegerButton.Y = 0;
        _aspireDashboardButton.Accepted += (_, e) =>
        {
            e.Handled = true;
            Dispatch(() => _controller.OpenKnownLinkAsync(KnownLinks.AspireDashboard, _sessionCancellation.Token));
        };
        _jaegerButton.Accepted += (_, e) =>
        {
            e.Handled = true;
            Dispatch(() => _controller.OpenKnownLinkAsync(KnownLinks.Jaeger, _sessionCancellation.Token));
        };

        Add(_topologyBar, _aspireDashboardButton, _jaegerButton, _navigation, _content, _evidenceStrip, statusBar);
        FrameChanged += (_, _) => ApplyResponsiveLayout();
        _controller.StateChanged += OnStateChanged;
        _ = PollAsync(_pollCancellation.Token);
    }

    private View BuildOperationsView()
    {
        var view = NewWorkspace("OPERATIONS");
        AddField(view, "From", _fromAccount, 0);
        AddField(view, "To", _toAccount, 1);
        AddField(view, "Amount", _amount, 2, 16);
        AddField(view, "Currency", _currency, 2, 45, 8);

        _railButton.X = 1;
        _railButton.Y = 3;
        _idempotencyButton.X = Pos.Right(_railButton) + 2;
        _idempotencyButton.Y = 3;
        _railButton.Accepted += (_, e) =>
        {
            e.Handled = true;
            _rail = _rail == PaymentRail.Standard ? PaymentRail.Instant : PaymentRail.Standard;
            _railButton.Text = $"Rail: {_rail.ToString().ToLowerInvariant()}";
        };
        _idempotencyButton.Accepted += (_, e) =>
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

        AddField(view, "Supplied key", _suppliedKey, 4);
        view.Add(new Label { X = 1, Y = 5, Text = "Omitted mode: not retry-safe after an ambiguous outcome." });

        _submitButton.X = 1;
        _submitButton.Y = 7;
        _resendButton.X = Pos.Right(_submitButton) + 2;
        _resendButton.Y = 7;
        _submitButton.Accepted += (_, e) => { e.Handled = true; Dispatch(SubmitPaymentAsync); };
        _resendButton.Accepted += (_, e) => { e.Handled = true; Dispatch(() => _controller.ResendLastPaymentAsync(_sessionCancellation.Token)); };

        AddField(view, "Burst count", _burstCount, 9, 16);
        AddField(view, "Concurrency", _burstConcurrency, 9, 45, 8);
        _burstButton.X = 1;
        _burstButton.Y = 10;
        _cancelBurstButton.X = Pos.Right(_burstButton) + 2;
        _cancelBurstButton.Y = 10;
        _burstStatus.X = 1;
        _burstStatus.Y = 11;
        _burstStatus.Width = Dim.Fill(1);
        _burstButton.Accepted += (_, e) => { e.Handled = true; Dispatch(RunBurstAsync); };
        _cancelBurstButton.Accepted += (_, e) => { e.Handled = true; _controller.CancelActiveBurst(); };

        AddField(view, "Outcome id/key", _outcomeKey, 13);
        _queryButton.X = 1;
        _queryButton.Y = 14;
        _queryButton.Accepted += (_, e) =>
        {
            e.Handled = true;
            Dispatch(() => _controller.QueryOutcomeAsync(_outcomeKey.Text.ToString() ?? string.Empty, _sessionCancellation.Token));
        };
        view.Add(_railButton, _idempotencyButton, _submitButton, _resendButton, _burstButton, _cancelBurstButton, _burstStatus, _queryButton);
        return view;
    }

    private View BuildResourcesView()
    {
        var view = NewWorkspace("RESOURCES");
        _resourceList.X = 1;
        _resourceList.Y = 1;
        _resourceList.Width = Dim.Fill(1);
        _resourceList.Height = Dim.Fill(7);

        _startRegularButton.X = 1;
        _startRegularButton.Y = Pos.AnchorEnd(5);
        _attachRegularButton.X = Pos.Right(_startRegularButton) + 1;
        _attachRegularButton.Y = Pos.AnchorEnd(5);
        _startLoadButton.X = Pos.Right(_attachRegularButton) + 1;
        _startLoadButton.Y = Pos.AnchorEnd(5);
        _attachLoadButton.X = Pos.Right(_startLoadButton) + 1;
        _attachLoadButton.Y = Pos.AnchorEnd(5);

        _stopButton.X = 1;
        _stopButton.Y = Pos.AnchorEnd(3);
        _switchButton.X = Pos.Right(_stopButton) + 1;
        _switchButton.Y = Pos.AnchorEnd(3);
        _resourceActionButton.X = Pos.Right(_switchButton) + 1;
        _resourceActionButton.Y = Pos.AnchorEnd(3);
        _refreshButton.X = Pos.Right(_resourceActionButton) + 1;
        _refreshButton.Y = Pos.AnchorEnd(3);

        _startRegularButton.Accepted += (_, e) => { e.Handled = true; Dispatch(() => _controller.StartAsync(TopologyProfile.Regular, _sessionCancellation.Token)); };
        _attachRegularButton.Accepted += (_, e) => { e.Handled = true; Dispatch(() => _controller.AttachAsync(TopologyProfile.Regular, _sessionCancellation.Token)); };
        _startLoadButton.Accepted += (_, e) => { e.Handled = true; Dispatch(() => _controller.StartAsync(TopologyProfile.LoadTests, _sessionCancellation.Token)); };
        _attachLoadButton.Accepted += (_, e) => { e.Handled = true; Dispatch(() => _controller.AttachAsync(TopologyProfile.LoadTests, _sessionCancellation.Token)); };
        _stopButton.Accepted += (_, e) =>
        {
            e.Handled = true;
            if (ConfirmDestructive("Stop owned AppHost", "Stop the exact AppHost child owned by this session?"))
            {
                Dispatch(() => _controller.StopAsync(_sessionCancellation.Token));
            }
        };
        _switchButton.Accepted += (_, e) =>
        {
            e.Handled = true;
            var target = _controller.State.Profile == TopologyProfile.Regular ? TopologyProfile.LoadTests : TopologyProfile.Regular;
            if (ConfirmDestructive($"Switch to {target}", $"Stop the owned AppHost and start {target}?"))
            {
                Dispatch(() => _controller.SwitchAsync(target, _sessionCancellation.Token));
            }
        };
        _resourceActionButton.Accepted += (_, e) =>
        {
            e.Handled = true;
            if (_resourceRows.Count == 0)
            {
                return;
            }

            var index = Math.Clamp(_resourceList.SelectedItem ?? 0, 0, _resourceRows.Count - 1);
            var row = _resourceRows[index];
            if (!Enum.TryParse<ResourceCommand>(row.NextAction, out var command))
            {
                return;
            }

            var exactCommand = $"aspire resource {row.Name} {row.NextAction.ToLowerInvariant()}";
            if (ConfirmDestructive($"{row.NextAction} {row.Name}", exactCommand))
            {
                Dispatch(() => _controller.ExecuteResourceCommandAsync(row.Name, command, _sessionCancellation.Token));
            }
        };
        _refreshButton.Accepted += (_, e) => { e.Handled = true; Dispatch(() => _controller.RefreshAsync(_sessionCancellation.Token)); };

        view.Add(
            _resourceList,
            _startRegularButton,
            _attachRegularButton,
            _startLoadButton,
            _attachLoadButton,
            _stopButton,
            _switchButton,
            _resourceActionButton,
            _refreshButton);
        return view;
    }

    private View BuildEvidenceView()
    {
        var view = NewWorkspace("EVIDENCE / RESULTS");
        _evidenceList.X = 1;
        _evidenceList.Y = 1;
        _evidenceList.Width = Dim.Percent(42);
        _evidenceList.Height = Dim.Fill(5);
        _evidenceDetail.X = Pos.Right(_evidenceList) + 1;
        _evidenceDetail.Y = 1;
        _evidenceDetail.Width = Dim.Fill(1);
        _evidenceDetail.Height = Dim.Fill(5);

        _detailsButton.X = 1;
        _detailsButton.Y = Pos.AnchorEnd(3);
        _wrapButton.X = Pos.Right(_detailsButton) + 1;
        _wrapButton.Y = Pos.AnchorEnd(3);
        _exportButton.X = Pos.Right(_wrapButton) + 1;
        _exportButton.Y = Pos.AnchorEnd(3);
        _inspectPaymentsOutbox.X = 1;
        _inspectPaymentsOutbox.Y = Pos.AnchorEnd(1);
        _inspectCoreBankInbox.X = Pos.Right(_inspectPaymentsOutbox) + 1;
        _inspectCoreBankInbox.Y = Pos.AnchorEnd(1);

        _detailsButton.Accepted += (_, e) =>
        {
            e.Handled = true;
            if (_evidenceRows.Count == 0)
            {
                return;
            }

            var index = Math.Clamp(_evidenceList.SelectedItem ?? 0, 0, _evidenceRows.Count - 1);
            _controller.SelectEvidence(_evidenceRows[index].Sequence);
        };
        _wrapButton.Accepted += (_, e) =>
        {
            e.Handled = true;
            _evidenceDetail.WordWrap = !_evidenceDetail.WordWrap;
            _wrapButton.Text = _evidenceDetail.WordWrap ? "Wrap: on" : "Wrap: off";
        };
        _exportButton.Accepted += (_, e) => { e.Handled = true; Dispatch(() => _controller.ExportEvidenceAsync(_sessionCancellation.Token)); };
        _inspectPaymentsOutbox.Accepted += (_, e) => { e.Handled = true; Dispatch(() => _controller.InspectAsync(KnownEndpoints.PaymentsOutbox, _sessionCancellation.Token)); };
        _inspectCoreBankInbox.Accepted += (_, e) => { e.Handled = true; Dispatch(() => _controller.InspectAsync(KnownEndpoints.CoreBankInbox, _sessionCancellation.Token)); };

        view.Add(_evidenceList, _evidenceDetail, _detailsButton, _wrapButton, _exportButton, _inspectPaymentsOutbox, _inspectCoreBankInbox);
        return view;
    }

    private View BuildLoadView()
    {
        var view = NewWorkspace("LOAD TEST");
        _loadPhase.X = 1;
        _loadPhase.Y = 1;
        _loadPhase.Width = Dim.Fill(1);
        _loadResults.X = 1;
        _loadResults.Y = 3;
        _loadResults.Width = Dim.Fill(1);
        _loadResults.Height = Dim.Fill(5);
        AddField(view, "Expected unique", _expectedUnique, 11, 20);
        _runLoadButton.X = 1;
        _runLoadButton.Y = Pos.AnchorEnd(2);
        _runLoadButton.Accepted += (_, e) =>
        {
            e.Handled = true;
            if (ConfirmDestructive("Run accepted load workflow", "Reset disposable LoadTests state, run k6, wait, assert, and investigate?"))
            {
                if (!int.TryParse(_expectedUnique.Text.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                    || value <= 0)
                {
                    _evidenceStrip.Text = "Expected unique count must be a positive integer; load run not started.";
                    return;
                }
                var expected = (int?)value;
                Dispatch(() => _controller.RunLoadTestAsync(expected, _sessionCancellation.Token));
            }
        };
        view.Add(_loadPhase, _loadResults, _runLoadButton);
        return view;
    }

    private async Task SubmitPaymentAsync()
    {
        if (!decimal.TryParse(_amount.Text.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
        {
            return;
        }

        var request = new PaymentRequest(
            _fromAccount.Text.ToString() ?? string.Empty,
            _toAccount.Text.ToString() ?? string.Empty,
            amount,
            _currency.Text.ToString() ?? string.Empty,
            _rail);
        await _controller.SubmitPaymentAsync(
            request,
            _idempotencyMode,
            _idempotencyMode == IdempotencyMode.Supplied ? _suppliedKey.Text.ToString() : null,
            _sessionCancellation.Token);
    }

    private async Task RunBurstAsync()
    {
        if (!decimal.TryParse(_amount.Text.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            || !int.TryParse(_burstCount.Text.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || !int.TryParse(_burstConcurrency.Text.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var concurrency))
        {
            return;
        }

        var request = new PaymentRequest(
            _fromAccount.Text.ToString() ?? string.Empty,
            _toAccount.Text.ToString() ?? string.Empty,
            amount,
            _currency.Text.ToString() ?? string.Empty,
            _rail);
        await _controller.RunBurstAsync(request, count, concurrency, _sessionCancellation.Token);
    }

    private Button CreateNavigationButton(string text, WorkspaceKind workspace, int y)
    {
        var button = new Button { Text = text, X = 1, Y = y, Width = Dim.Fill(1) };
        button.Accepted += (_, e) =>
        {
            e.Handled = true;
            ActivateWorkspace(workspace);
        };
        return button;
    }

    private static View NewWorkspace(string title) =>
        new FrameView { Title = title, X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };

    private static void AddField(View parent, string label, TextField field, int y, int labelWidth = 16, int fieldWidth = 38)
    {
        parent.Add(new Label { X = 1, Y = y, Width = labelWidth, Text = label });
        field.X = labelWidth + 2;
        field.Y = y;
        field.Width = fieldWidth;
        parent.Add(field);
    }

    private void ActivateWorkspace(WorkspaceKind workspace) => _controller.SelectWorkspace(workspace);

    private void OnStateChanged(OperatorConsoleState state) =>
        AppTerminal.Invoke(() => Render(PresentationModelBuilder.Build(state)));

    public async Task RefreshAsync()
    {
        await _controller.InitializeAsync(_sessionCancellation.Token);
        Render(PresentationModelBuilder.Build(_controller.State));
    }

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
                AppTerminal.Invoke(() => _evidenceStrip.Text = $"Unreachable — {ex.Message}");
            }
        }
    }

    private void Render(OperatorPresentationModel model)
    {
        _topologyBar.Text = model.TopologyBar;
        _operationsView.Visible = model.ActiveWorkspace == WorkspaceKind.Operations;
        _resourcesView.Visible = model.ActiveWorkspace == WorkspaceKind.Resources;
        _evidenceView.Visible = model.ActiveWorkspace == WorkspaceKind.Evidence;
        _loadView.Visible = model.ActiveWorkspace == WorkspaceKind.LoadTest;

        _resourceRows = model.Resources;
        _resourceList.SetSource(new ObservableCollection<string>(
            model.Resources.Count == 0
                ? ["○ No verified resources — refresh or attach a known topology"]
                : model.Resources.Select(row => $"{row.Symbol} {row.Name,-20} {row.State,-11} {row.Detail} [{row.NextAction}]")));
        _evidenceRows = model.Evidence;
        _evidenceList.SetSource(new ObservableCollection<string>(
            model.Evidence.Count == 0
                ? ["○ No actions yet this session"]
                : model.Evidence.Select(row => $"{row.Summary} · {row.Provenance}")));
        _evidenceDetail.Text = model.SelectedEvidenceDetail;
        _evidenceStrip.Text = model.EvidenceStrip;
        _burstStatus.Text = model.BurstStatus;
        _loadPhase.Text = $"Reset → Run → Wait → Assert → Investigate{Environment.NewLine}{model.LoadPhaseStatus}";
        _loadResults.SetSource(new ObservableCollection<string>(model.LoadResults));

        _submitButton.Enabled = !model.IsBusy;
        _resendButton.Enabled = model.CanResend;
        _burstButton.Enabled = !model.IsBusy;
        _cancelBurstButton.Enabled = model.CanCancelBurst;
        _startRegularButton.Enabled = !model.IsBusy && _controller.State.Profile == TopologyProfile.None;
        _attachRegularButton.Enabled = !model.IsBusy && _controller.State.Ownership == TopologyOwnership.None;
        _startLoadButton.Enabled = !model.IsBusy && _controller.State.Profile == TopologyProfile.None;
        _attachLoadButton.Enabled = !model.IsBusy && _controller.State.Ownership == TopologyOwnership.None;
        _stopButton.Enabled = model.CanStopOrSwitch;
        _switchButton.Enabled = model.CanStopOrSwitch;
        _resourceActionButton.Enabled = !model.IsBusy && model.Resources.Any(row => row.CanMutate);
        _runLoadButton.Enabled = model.CanUseLoadTest;
        _queryButton.Enabled = true;
        _aspireDashboardButton.Enabled = _controller.State.Topology?.DashboardUrl is not null;
        _jaegerButton.Enabled = _controller.State.Profile != TopologyProfile.None;
        _detailsButton.Enabled = true;
        _wrapButton.Enabled = true;
    }

    private void ApplyResponsiveLayout()
    {
        var layout = InteractionPolicies.LayoutFor(Frame.Width, Frame.Height);
        var compact = layout != TerminalLayoutMode.Preferred;
        _navigation.Width = compact ? 5 : 18;
        _content.X = Pos.Right(_navigation);
        for (var index = 0; index < _navigationButtons.Length; index++)
        {
            _navigationButtons[index].Text = compact
                ? (index + 1).ToString(CultureInfo.InvariantCulture)
                : $"{index + 1} {PresentationModelBuilder.Build(_controller.State).Navigation[index].Label}";
        }

        if (layout == TerminalLayoutMode.BelowMinimum)
        {
            _evidenceStrip.Text = "Terminal below 80×24 — use keyboard shortcuts; session state is preserved.";
        }
    }

    private static bool ConfirmDestructive(string title, string command)
    {
        var dialog = new Dialog<bool> { Title = title, Width = 66, Height = 8 };
        OperatorTheme.Apply(dialog, OperatorTheme.OverlayScheme);
        var cancel = new Button { Text = "Cancel", IsDefault = true };
        cancel.Accepted += (_, e) =>
        {
            e.Handled = true;
            dialog.Result = false;
            dialog.RequestStop();
        };
        dialog.Add(new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = 2,
            Text = $"{command}{Environment.NewLine}Press Y to confirm. Escape cancels.",
        });
        dialog.AddButton(cancel);
        dialog.KeyDown += (_, key) =>
        {
            if (InteractionPolicies.ConfirmsDestructiveAction((char)(uint)key))
            {
                key.Handled = true;
                dialog.Result = true;
                dialog.RequestStop();
            }
        };
        cancel.SetFocus();
        AppTerminal.Run(dialog);
        return dialog.Result == true;
    }

    private async Task RequestExitAsync()
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
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or HttpRequestException)
        {
        }

        await _onExitRequested();
    }

    private void Dispatch(Func<Task> action)
    {
        var task = action();
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
                    AppTerminal.Invoke(() => _evidenceStrip.Text = $"Operation failed — {task.Exception.GetBaseException().Message}");
                }
            },
            TaskScheduler.Default);
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
        }

        base.Dispose(disposing);
    }
}
#pragma warning restore CS0618
