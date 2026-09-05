using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Infrastructure;
using CoreBankDemo.DemoRunner.Terminal;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Terminal.Gui.Input;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

public class MainWindowTests
{
    [Theory]
    [InlineData(1, WorkspaceKind.Operations)]
    [InlineData(2, WorkspaceKind.Resources)]
    [InlineData(3, WorkspaceKind.Evidence)]
    [InlineData(4, WorkspaceKind.LoadTest)]
    public void Shortcuts_OneThroughFour_SelectEveryWorkspace(int number, WorkspaceKind expected)
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        var key = number switch
        {
            1 => Key.D1,
            2 => Key.D2,
            3 => Key.D3,
            4 => Key.D4,
            _ => throw new ArgumentOutOfRangeException(nameof(number)),
        };

        window.HandleKeyForTest(key);

        controller.State.ActiveWorkspace.Should().Be(expected);
        window.IsWorkspaceVisible(expected).Should().BeTrue();
    }

    [Fact]
    public void Resize_At80x24_KeepsCompactRailAndAllWorkspaceShortcutsReachable()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);

        window.ResizeForTest(80, 24);
        foreach (var key in new[] { Key.D1, Key.D2, Key.D3, Key.D4 })
        {
            window.HandleKeyForTest(key);
        }

        window.NavigationFrameWidth.Should().Be(5);
        controller.State.ActiveWorkspace.Should().Be(WorkspaceKind.LoadTest);
        window.IsWorkspaceVisible(WorkspaceKind.LoadTest).Should().BeTrue();
    }

    [Fact]
    public async Task InvalidPaymentAndBurstFields_SurfaceActionableMessages()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        window.AmountField.Text = "1,50";

        await window.TriggerSubmitForTestAsync();

        window.LastUiMessage.Should().Contain("decimal").And.Contain("separator");

        window.AmountField.Text = "1.00";
        window.BurstCountField.Text = "not-a-count";
        await window.TriggerBurstForTestAsync();

        window.LastUiMessage.Should().Contain("Burst requires");
    }

    [Fact]
    public async Task RejectedControllerResult_IsNeverSilent()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);

        await window.TriggerResendForTestAsync();

        window.LastUiMessage.Should().Contain("No retry-safe");
    }

    [Fact]
    public async Task PaymentValidationReasons_AreShownAtFieldLevel()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        window.FromAccountField.Text = "short";
        window.ToAccountField.Text = "short";
        window.CurrencyField.Text = "eur";
        window.AmountField.Text = "0";
        window.SetIdempotencyModeForTest(IdempotencyMode.Supplied);

        await window.TriggerSubmitForTestAsync();

        window.LastUiMessage.Should().Contain("From account")
            .And.Contain("To account")
            .And.Contain("Amount")
            .And.Contain("Currency");
    }

    [Fact]
    public async Task QueryInspectLoadAndExportFailures_AreAllSurfaced()
    {
        var harness = new OperatorHarness();
        harness.Exporter.Result = new EvidenceExportResult(false, "file", "disk full");
        var controller = harness.CreateController();
        using var window = CreateWindow(controller);

        await window.TriggerQueryForTestAsync();
        window.LastUiMessage.Should().Contain("Start or attach");

        await window.TriggerInspectForTestAsync(KnownEndpoints.PaymentsOutbox);
        window.LastUiMessage.Should().Contain("Start or attach");

        await window.TriggerLoadForTestAsync(100);
        window.LastUiMessage.Should().Contain("Load Test requires");

        await window.TriggerExportForTestAsync();
        window.LastUiMessage.Should().Be("disk full");
    }

    [Fact]
    public async Task ValidBurstFromMainWindow_DisplaysControllerResultWithoutSilentExit()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.BurstCountField.Text = "2";
        window.BurstConcurrencyField.Text = "1";

        await window.TriggerBurstForTestAsync();

        controller.State.Burst.Sent.Should().Be(2);
        window.LastUiMessage.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAndOrderlyExit_RunThroughActualMainWindowPaths()
    {
        var harness = new OperatorHarness();
        var controller = harness.CreateController();
        var exited = false;
        using var window = new MainWindow(
            controller,
            () =>
            {
                exited = true;
                return Task.CompletedTask;
            },
            new FakeConfirmationService(),
            startPolling: false,
            marshalUpdates: false);

        await window.RefreshAsync();
        await window.RequestExitAsync();

        controller.State.Preflight.Should().NotBeNull();
        exited.Should().BeTrue();
    }

    [Fact]
    public async Task DiscoveryFailure_DisablesBothStartControlsAndShowsUnreachable()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Discovery = TopologyDiscoveryResult.Unreachable("aspire ps timeout");
        var controller = harness.CreateController();
        using var window = CreateWindow(controller);

        await window.RefreshAsync();

        window.StartRegularEnabled.Should().BeFalse();
        window.StartLoadTestsEnabled.Should().BeFalse();
        controller.State.StatusLine.Should().Contain("Unreachable");
    }

    [Fact]
    public async Task LoadRunEnabled_RequiresFreshReadyLoadTopology()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.LoadTests));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.LoadTests, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.RenderForTest();

        window.LoadRunEnabled.Should().BeTrue();

        harness.Time.Advance(TimeSpan.FromSeconds(6));
        await controller.RunLoadTestAsync(100, CancellationToken.None);
        window.RenderForTest();

        window.LoadRunEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ResourceConfirmation_ListsExactInstancesAndReturnsFocusToTrigger()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        controller.SelectWorkspace(WorkspaceKind.Resources);
        var confirmation = new FakeConfirmationService { Result = false };
        using var window = CreateWindow(controller, confirmation);
        window.RenderForTest();
        window.SelectResourceForTest(KnownResources.CoreBankApi);
        window.ResourceActionButton.SetFocus();

        window.TriggerResourceActionForTest();

        confirmation.Requests.Should().ContainSingle();
        confirmation.Requests[0].Instances.Should().HaveCount(2);
        confirmation.Requests[0].Command.Should().Contain("aspire resource corebank-api-1 stop")
            .And.Contain("aspire resource corebank-api-2 stop");
        window.ResourceActionButton.HasFocus.Should().BeTrue();
    }

    [Fact]
    public async Task HealthyResource_ExposesIntentionalRestartControl()
    {
        var harness = new OperatorHarness();
        var controller = harness.CreateController();
        using var window = CreateWindow(controller);
        var state = OperatorHarness.Snapshot(TopologyProfile.Regular);
        harness.Aspire.Queue(state);
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        controller.SelectWorkspace(WorkspaceKind.Resources);
        window.RenderForTest();

        window.RestartResourceButton.Enabled.Should().BeTrue();
    }

    [Fact]
    public void DestructiveDialog_OnlyUppercaseYConfirmsExactlyOnce()
    {
        var request = new ConfirmationRequest("Restart", "aspire resource x restart", ["x"]);

        using var lower = new DestructiveConfirmationDialog(request);
        lower.HandleKeyForTest(Key.Y);
        lower.Result.Should().BeFalse();
        lower.ConfirmationCount.Should().Be(0);

        using var enter = new DestructiveConfirmationDialog(request);
        enter.FocusCancel();
        enter.CancelButton.HasFocus.Should().BeTrue();
        enter.HandleKeyForTest(Key.Enter);
        enter.Result.Should().BeFalse();

        using var escape = new DestructiveConfirmationDialog(request);
        escape.HandleKeyForTest(Key.Esc);
        escape.Result.Should().BeFalse();

        using var upper = new DestructiveConfirmationDialog(request);
        upper.HandleKeyForTest(Key.Y.WithShift);
        upper.HandleKeyForTest(Key.Y.WithShift);
        upper.Result.Should().BeTrue();
        upper.ConfirmationCount.Should().Be(1);
        upper.DefaultAcceptView.Should().BeSameAs(upper.CancelButton);
    }

    [Fact]
    public void AcceptCommand_ReachesNonDefaultButtons()
    {
        // Terminal.Gui raises Accepted only for the default button; a click on any other
        // button raises Accepting. Handlers wired to Accepted leave every non-default
        // control inert, which is what this asserts against.
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        window.RenderForTest();

        window.RailButton.InvokeCommand(Command.Accept);
        window.IdempotencyButton.InvokeCommand(Command.Accept);
        window.WrapButton.InvokeCommand(Command.Accept);
        window.DetailsButton.InvokeCommand(Command.Accept);

        window.RailButton.Text.Should().Be("Rail: instant");
        window.IdempotencyButton.Text.Should().Be("Idempotency: Supplied");
        window.WrapButton.Text.Should().Be("Wrap: on");
        window.LastUiMessage.Should().Contain("No action has been recorded");
    }

    [Fact]
    public void SuppliedKeyDefault_IsUniquePerSessionSoItNeverReplaysAnOldRow()
    {
        var controller = new OperatorHarness().CreateController();
        using var first = CreateWindow(controller);
        using var second = CreateWindow(controller);

        first.SuppliedKeyField.Text.ToString().Should().StartWith("demo-key-").And.NotBe("demo-key-001");
        second.SuppliedKeyField.Text.ToString().Should().NotBe(first.SuppliedKeyField.Text.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenJaegerLink_AlwaysCopiesTheResolvedUrlToTheTerminalClipboard(bool osBrowserOpens)
    {
        // Copying always happens regardless of whether the OS-level browser
        // launch itself succeeded: there is essentially never a default
        // browser reachable from this sandbox, so a fallback gated on failure
        // would never fire, and a modal popup here was found not to render at
        // all when driven through the real async/background-thread path.
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        harness.Browser.NextSucceeds = osBrowserOpens;
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        var terminal = new StringWriter();
        window.TerminalOut = terminal;

        await window.TriggerOpenKnownLinkForTestAsync("Jaeger", KnownLinks.Jaeger);

        terminal.ToString().Should().StartWith("]52;c;").And.EndWith("");
        window.LastUiMessage.Should().Contain("terminal clipboard").And.Contain(EndpointResolver.LinkFor(KnownLinks.Jaeger));
    }

    [Fact]
    public async Task OpenAspireLink_WhenNoDashboardUrlIsVerifiedYet_SaysSoAndCopiesNothing()
    {
        using var window = CreateWindow(new OperatorHarness().CreateController());
        var terminal = new StringWriter();
        window.TerminalOut = terminal;

        await window.TriggerOpenKnownLinkForTestAsync("Aspire dashboard", KnownLinks.AspireDashboard);

        terminal.ToString().Should().BeEmpty();
        window.LastUiMessage.Should().Contain("not available yet");
    }

    [Fact]
    public async Task CopyDetail_WritesTheDetailToTheTerminalClipboardAndSaysSo()
    {
        // Terminal.Gui's built-in copy shells out to an OS clipboard helper and
        // silently does nothing when none can reach a display server -- the
        // sandbox and every SSH session. OSC 52 goes to the terminal instead.
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        var terminal = new StringWriter();
        window.TerminalOut = terminal;
        await window.RefreshAsync();
        window.DetailsButton.InvokeCommand(Command.Accept);
        window.RenderForTest();

        window.CopyButton.InvokeCommand(Command.Accept);

        terminal.ToString().Should().StartWith("\u001b]52;c;").And.EndWith("\u0007");
        window.LastUiMessage.Should().Contain("terminal clipboard");
    }

    [Fact]
    public void CopyDetail_WithNothingSelected_SaysSoInsteadOfCopyingNothing()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        var terminal = new StringWriter();
        window.TerminalOut = terminal;
        window.RenderForTest();

        window.CopyButton.InvokeCommand(Command.Accept);

        terminal.ToString().Should().BeEmpty();
        window.LastUiMessage.Should().Contain("nothing to copy");
    }

    [Fact]
    public async Task FailureMessage_SurvivesTheNextPollRender()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);

        await window.TriggerResendForTestAsync();
        window.RenderForTest();
        window.RenderForTest();

        window.MessageLineText.Should().Contain("No retry-safe");
        window.StatusLineText.Should().Be(controller.State.StatusLine);
    }

    [Fact]
    public async Task ResourceSelection_SurvivesRepeatedRenders()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        controller.SelectWorkspace(WorkspaceKind.Resources);
        using var window = CreateWindow(controller);
        window.RenderForTest();
        window.SelectResourceForTest(KnownResources.CoreBankApi);
        var selected = window.ResourceList.SelectedItem;

        window.RenderForTest();
        window.RenderForTest();

        selected.Should().NotBe(0);
        window.ResourceList.SelectedItem.Should().Be(selected);
    }

    [Fact]
    public void OnlyTheActiveWorkspaceIsMounted()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);

        foreach (var workspace in Enum.GetValues<WorkspaceKind>())
        {
            controller.SelectWorkspace(workspace);
            window.RenderForTest();

            window.MountedWorkspaceCount.Should().Be(1);
            window.IsWorkspaceVisible(workspace).Should().BeTrue();
        }
    }

    private static MainWindow CreateWindow(
        OperatorConsoleController controller,
        IConfirmationService? confirmation = null) =>
        new(controller, () => Task.CompletedTask, confirmation, startPolling: false, marshalUpdates: false);

    private sealed class FakeConfirmationService : IConfirmationService
    {
        public bool Result { get; init; }
        public List<ConfirmationRequest> Requests { get; } = [];

        public bool Confirm(ConfirmationRequest request)
        {
            Requests.Add(request);
            return Result;
        }
    }
}
