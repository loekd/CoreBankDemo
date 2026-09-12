using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Infrastructure;
using CoreBankDemo.DemoRunner.Terminal;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.Views;
using Xunit;
using CoreBankDemo.DemoRunner.Tests;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

[Collection(OperatorThemeCollection.Name)]
public class MainWindowTests
{
    [Theory]
    [InlineData(1, WorkspaceKind.Operations)]
    [InlineData(2, WorkspaceKind.Resources)]
    [InlineData(3, WorkspaceKind.Evidence)]
    [InlineData(4, WorkspaceKind.LoadTest)]
    [InlineData(5, WorkspaceKind.Faults)]
    public void Shortcuts_OneThroughFive_SelectEveryWorkspace(int number, WorkspaceKind expected)
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        var key = number switch
        {
            1 => Key.D1,
            2 => Key.D2,
            3 => Key.D3,
            4 => Key.D4,
            5 => Key.D5,
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
        foreach (var key in new[] { Key.D1, Key.D2, Key.D3, Key.D4, Key.D5 })
        {
            window.HandleKeyForTest(key);
        }

        window.NavigationFrameWidth.Should().Be(5);
        controller.State.ActiveWorkspace.Should().Be(WorkspaceKind.Faults);
        window.IsWorkspaceVisible(WorkspaceKind.Faults).Should().BeTrue();
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
        window.AmountField.Text = "0";
        window.SetIdempotencyModeForTest(IdempotencyMode.Supplied);

        await window.TriggerSubmitForTestAsync();

        window.LastUiMessage.Should().Contain("From account")
            .And.Contain("To account")
            .And.Contain("Amount");
        window.LastUiMessage.Should().NotContain("Currency",
            "there is no currency input on screen and the console always sends EUR");
    }

    [Fact]
    public async Task QueryInspectLoadAndExportFailures_AreAllSurfaced()
    {
        var harness = new OperatorHarness();
        harness.Exporter.Result = new EvidenceExportResult(false, "file", "disk full");
        var controller = harness.CreateController();
        using var window = CreateWindow(controller);

        // The lookup is anchored to the card, so with no payment on it the console says exactly
        // that -- as a notice, because nothing was attempted and nothing failed.
        await window.TriggerQueryForTestAsync();
        window.LastUiMessage.Should().Contain("nothing to look up");
        window.AnnouncementIsFailureToned.Should().BeFalse("no verdict was reached, so none is announced");
        controller.State.Evidence.Should().BeEmpty("nothing was attempted against a payment");

        await window.TriggerInspectForTestAsync(KnownEndpoints.PaymentsOutbox);
        window.LastUiMessage.Should().Contain("Start or attach");
        window.AnnouncementIsFailureToned.Should().BeTrue("a command that ran and was refused is a proven failure");
        // The refusal never became a request, so nothing else in the system will ever record it.
        var inspect = controller.State.Evidence.Last();
        inspect.Summary.Should().Contain("refused").And.Contain("Start or attach");
        inspect.Succeeded.Should().BeFalse();
        inspect.Method.Should().Be("(refused by the console)");

        await window.TriggerLoadForTestAsync(100);
        window.LastUiMessage.Should().Contain("Load Test requires");

        await window.TriggerExportForTestAsync();
        window.LastUiMessage.Should().Be("disk full");
    }

    /// <summary>
    /// The lookup is the deliberate second opinion on the card's own payment: read-only, never
    /// blocked by the single-action-in-flight rule, and reachable from the keyboard even while
    /// the card's one slot is carrying Cancel payment.
    /// </summary>
    [Fact]
    public async Task OutcomeLookup_ReachesTheCardsOwnPayment_FromTheSlotAndFromTheKeyboard()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "payment-id", "tx-8821", "Pending", "{}", null, TimeSpan.Zero));
        await controller.SubmitPaymentAsync(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Instant),
            IdempotencyMode.Supplied,
            "tx-8821",
            CancellationToken.None);
        window.RenderForTest();

        // The slot is carrying Cancel payment, so the keyboard is the only path to the lookup.
        window.FocusCard.ActionLabel.Should().Be(CardActions.Cancel);
        window.HandleKeyForTest(Key.O).Should().BeTrue();
        await window.LastDispatchedTask!;

        harness.Payments.QueryProfiles.Should().ContainSingle();
        controller.State.Evidence.Last().Summary.Should().Contain("Outcome query");

        // A proven payment whose key is not safe to reuse carries the lookup in the slot itself,
        // and firing it is the same call.
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Ambiguous, 0, null, "tx-8822", null, null, "reply lost", TimeSpan.Zero));
        await controller.SubmitPaymentAsync(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Instant),
            IdempotencyMode.Omitted,
            null,
            CancellationToken.None);
        harness.Feed.PushCompleted("tx-8822", new DateTimeOffset(2026, 8, 29, 12, 4, 31, TimeSpan.Zero));
        window.RenderForTest();

        controller.State.CanResendLastPayment.Should().BeFalse("an Omitted key is never safe to reuse");
        window.FocusCard.ActionLabel.Should().Be(CardActions.LookUpOutcome);

        window.TriggerCardActionForTest();
        await window.LastDispatchedTask!;

        harness.Payments.QueryProfiles.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValidBurstFromMainWindow_DisplaysControllerResultWithoutSilentExit()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);
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

    /// <summary>
    /// The headline control. After the operator stops the bank, the same control in the same
    /// place must read <c>Start corebank-api</c> and be pressable.
    /// </summary>
    [Fact]
    public async Task StoppedResource_ActionControlNamesStartAndIsEnabled()
    {
        var (controller, window) = await ResourcesWindowAsync(StoppedCoreBank());
        window.SelectResourceForTest(KnownResources.CoreBankApi);

        window.ResourceActionButton.Text.Should().Be("Start corebank-api");
        window.ResourceActionButton.Enabled.Should().BeTrue();
        window.ResourceActionButton.Text.Length.Should().BeLessThanOrEqualTo(MainWindow.ActionCaptionBudget);
        controller.State.Topology!.IsFingerprintMatch.Should().BeFalse("the stop moved the shape");
    }

    /// <summary>PRD FR20: Start destroys nothing, so it fires without the modal.</summary>
    [Fact]
    public async Task Start_DispatchesWithoutConfirmation_WhileStopStillAsks()
    {
        var confirmation = new FakeConfirmationService { Result = false };
        var (_, window) = await ResourcesWindowAsync(StoppedCoreBank(), confirmation);
        window.SelectResourceForTest(KnownResources.CoreBankApi);

        window.TriggerResourceActionForTest();

        confirmation.Requests.Should().BeEmpty("Start is the demo beat that has to land cleanly");
        window.LastDispatchedTask.Should().NotBeNull();
    }

    [Fact]
    public async Task Stop_StillAsksForConfirmationAndNamesItsExactCommands()
    {
        var confirmation = new FakeConfirmationService { Result = false };
        var (_, window) = await ResourcesWindowAsync(healthy: true, confirmation: confirmation);
        window.SelectResourceForTest(KnownResources.CoreBankApi);

        window.ResourceActionButton.Text.Should().Be("Stop corebank-api");
        window.TriggerResourceActionForTest();

        confirmation.Requests.Should().ContainSingle();
        confirmation.Requests[0].Command.Should().Contain("aspire resource corebank-api-1 stop");
    }

    /// <summary>
    /// FR16: the caption follows the selection, not the press. Nothing observed selection
    /// changes on this list before.
    /// </summary>
    [Fact]
    public async Task MovingTheSelection_RecaptionsTheActionControl()
    {
        var (_, window) = await ResourcesWindowAsync(StoppedCoreBank());
        window.SelectResourceForTest(KnownResources.CoreBankApi);
        var stopped = window.ResourceActionButton.Text;

        window.SelectResourceForTest(KnownResources.PaymentsApi);

        stopped.Should().Be("Start corebank-api");
        window.ResourceActionButton.Text.Should().Be("Stop payments-api");
    }

    /// <summary>
    /// FR17: a row with no legal action disables the control on that basis and says why —
    /// and the caption never prints the <c>Unavailable</c> sentinel as if it were a verb.
    /// </summary>
    [Fact]
    public async Task RowWithNoLegalAction_DisablesTheControlAndExplainsItself()
    {
        var (_, window) = await ResourcesWindowAsync(CoreBankIn(ResourceCondition.Starting));
        window.SelectResourceForTest(KnownResources.CoreBankApi);

        window.ResourceActionButton.Enabled.Should().BeFalse();
        window.ResourceActionButton.Text.Should().Be("Resource action");
        window.ResourceActionButton.Text.Should().NotContain("Unavailable");
        window.ResourcesHintText.Should().Contain("corebank-api").And.Contain("no lifecycle action");
    }

    /// <summary>FR19: the two controls never read the same words.</summary>
    [Fact]
    public async Task FailedResource_LeavesTheRestartControlStoodDown()
    {
        var (_, window) = await ResourcesWindowAsync(CoreBankIn(ResourceCondition.Failed));
        window.SelectResourceForTest(KnownResources.CoreBankApi);

        window.ResourceActionButton.Text.Should().Be("Restart corebank-api");
        window.RestartResourceButton.Text.Should().Be("Restart selected");
        window.RestartResourceButton.Enabled.Should().BeFalse();
        window.ResourceActionButton.Text.Should().NotBe(window.RestartResourceButton.Text);
    }

    /// <summary>
    /// The longest name a row can carry. <c>Start loadtest-initializer</c> does not fit the
    /// column, so the caption falls back to the verb rather than being truncated into a name
    /// no resource has.
    /// </summary>
    [Fact]
    public async Task CaptionTooLongForTheColumn_FallsBackToTheVerbAlone()
    {
        var (_, window) = await ResourcesWindowAsync(
            OperatorHarness.Snapshot(
                TopologyProfile.LoadTests,
                fingerprint: false,
                resources:
                [
                    new ResourceSnapshot(
                        KnownResources.LoadTestInitializer,
                        ResourceCondition.Stopped,
                        "Stopped",
                        [],
                        1,
                        InstanceNames: [KnownResources.LoadTestInitializer],
                        AllowedCommands: Enum.GetValues<ResourceCommand>().ToHashSet()),
                    .. OperatorHarness.DefaultResources(TopologyProfile.LoadTests)
                        .Where(resource => resource.Name != KnownResources.LoadTestInitializer),
                ]),
            profile: TopologyProfile.LoadTests);
        window.SelectResourceForTest(KnownResources.LoadTestInitializer);

        $"Start {KnownResources.LoadTestInitializer}".Length.Should()
            .BeGreaterThan(MainWindow.ActionCaptionBudget, "this is the case the fallback exists for");
        window.ResourceActionButton.Text.Should().Be("Start");
        window.ResourceActionButton.Enabled.Should().BeTrue();
    }

    /// <summary>FR22: Switch topology is gone; changing profile is Stop AppHost then Start.</summary>
    [Fact]
    public async Task ResourcesActionColumn_NoLongerCarriesASwitchControl()
    {
        var (_, window) = await ResourcesWindowAsync(healthy: true);

        window.ResourceActionCaptions.Should().NotContain(caption => caption.Contains("Switch"));
        window.ResourceActionCaptions.Should().Contain("Stop AppHost")
            .And.Contain("Start Regular")
            .And.Contain("Start LoadTests");
        // The arming button carries its own long read-only explanation and is not one of the
        // lifecycle controls; every control the operator drives the topology with fits.
        window.ResourceActionCaptions
            .Where(caption => caption.StartsWith("Start", StringComparison.Ordinal)
                || caption.StartsWith("Stop", StringComparison.Ordinal)
                || caption.StartsWith("Attach", StringComparison.Ordinal)
                || caption.StartsWith("Restart", StringComparison.Ordinal)
                || caption.StartsWith("Refresh", StringComparison.Ordinal)
                || caption.StartsWith("Resource", StringComparison.Ordinal))
            .Should().OnlyContain(
                caption => caption.Length <= MainWindow.ActionCaptionBudget,
                "every lifecycle caption has to fit the action column");
    }

    private static ResourceSnapshot[] StoppedCoreBank() => CoreBankIn(ResourceCondition.Stopped);

    private static ResourceSnapshot[] CoreBankIn(ResourceCondition condition) =>
    [
        new ResourceSnapshot(
            KnownResources.CoreBankApi,
            condition,
            condition.ToString(),
            [],
            condition == ResourceCondition.Stopped ? 0 : 2,
            InstanceNames: ["corebank-api-1", "corebank-api-2"],
            AllowedCommands: Enum.GetValues<ResourceCommand>().ToHashSet()),
        .. OperatorHarness.DefaultResources(TopologyProfile.Regular)
            .Where(resource => resource.Name != KnownResources.CoreBankApi),
    ];

    private static Task<(OperatorConsoleController Controller, MainWindow Window)> ResourcesWindowAsync(
        ResourceSnapshot[] resources,
        IConfirmationService? confirmation = null) =>
        ResourcesWindowAsync(
            OperatorHarness.Snapshot(TopologyProfile.Regular, fingerprint: false, resources: resources),
            confirmation);

    private static async Task<(OperatorConsoleController Controller, MainWindow Window)> ResourcesWindowAsync(
        TopologySnapshot? snapshot = null,
        IConfirmationService? confirmation = null,
        bool healthy = false,
        TopologyProfile profile = TopologyProfile.Regular)
    {
        var harness = new OperatorHarness();
        var attached = healthy || snapshot is null
            ? OperatorHarness.Snapshot(profile)
            : snapshot;
        harness.Aspire.Queue(OperatorHarness.Snapshot(profile));
        var controller = harness.CreateController();
        (await controller.AttachAsync(profile, CancellationToken.None)).Succeeded.Should().BeTrue();
        if (!healthy && snapshot is not null)
        {
            harness.Aspire.Queue(attached);
            await controller.RefreshAsync(CancellationToken.None);
        }

        controller.SelectWorkspace(WorkspaceKind.Resources);
        var window = CreateWindow(controller, confirmation);
        window.RenderForTest();
        return (controller, window);
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

        window.RailButton.Text.Should().Be("Rail ‹ instant ›");
        window.IdempotencyButton.Text.Should().Be("Key ‹ Supplied ›");
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

        window.AnnouncementLineText.Should().Contain("No retry-safe");
        controller.State.Evidence.Should().Contain(
            record => record.Summary.Contains("No retry-safe") && !record.Succeeded,
            "the removal of the bottom band is conditional on every refusal being an Evidence "
            + "record before its announcement is drawn");
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


    [Fact]
    public async Task ArrivingOutcome_ResolvesThePaymentRowInPlaceWithoutMovingTheList()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "payment-id", "tx-8821", "Pending", "{}", null, TimeSpan.FromMilliseconds(5)));
        await controller.SubmitPaymentAsync(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Generated,
            null,
            CancellationToken.None);
        window.RenderForTest();
        window.FocusCard.StateWord.Should().Be("AWAITING SETTLEMENT");

        harness.Feed.PushCompleted("tx-8821", new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        harness.Feed.PushBalance("tx-8821", "1001", -250m, 4750m);
        window.RenderForTest();

        controller.State.TrackedPayments.Single().State.Should().Be(PaymentTrackingState.Settled);
        window.FocusCard.TransactionId.Should().Be("tx-8821", "the card updates in place");
        window.FocusCard.StateWord.Should().Be("SETTLED");
        window.FocusCard.Closing.Should().Contain(line => line.Contains("−250.00 → 4,750.00 EUR"));
        window.StillOpenVisible.Should().BeFalse("the payment left the strip the moment it was proven");
        window.FeedStatusText.Should().Contain("Listening since");
    }

    [Fact]
    public async Task StripSelection_RePointsTheFocusCardAndNothingElseDoes()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "payment-id", "tx-8821", "Pending", "{}", null, TimeSpan.FromMilliseconds(5)));
        await controller.SubmitPaymentAsync(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Generated,
            null,
            CancellationToken.None);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "payment-id", "tx-8822", "Pending", "{}", null, TimeSpan.FromMilliseconds(5)));
        await controller.SubmitPaymentAsync(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 80m, "EUR", PaymentRail.Instant),
            IdempotencyMode.Supplied,
            "tx-8822",
            CancellationToken.None);
        window.RenderForTest();

        window.StillOpenVisible.Should().BeTrue("two payments are open");
        window.FocusCard.TransactionId.Should().Be("tx-8822", "a new submission takes the selection");

        window.SelectStillOpenRowForTest(0);
        window.RenderForTest();

        window.FocusCard.TransactionId.Should().Be("tx-8821");
        window.StillOpenRowTexts.Should().Contain(line => line.Contains("…4567"),
            "the strip truncates identifiers to their last four digits, and only the strip does");
    }

    /// <summary>
    /// An arriving outcome updates the row in place and never re-points the card: the operator
    /// may be mid-sentence with a finger on the line they chose.
    /// </summary>
    [Fact]
    public async Task ArrivingOutcome_LeavesTheCardOnTheOperatorsOwnSelection()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);
        foreach (var id in new[] { "tx-8821", "tx-8822", "tx-8823" })
        {
            harness.Payments.Queue(new PaymentResult(
                PaymentOutcome.Pending, 202, "payment-id", id, "Pending", "{}", null, TimeSpan.Zero));
            await controller.SubmitPaymentAsync(
                new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Standard),
                IdempotencyMode.Supplied,
                id,
                CancellationToken.None);
        }

        window.RenderForTest();
        window.SelectStillOpenRowForTest(0);
        window.RenderForTest();
        var linesBefore = window.StillOpenRowTexts.Count;

        harness.Feed.PushCompleted("tx-8823", new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        window.RenderForTest();

        window.FocusCard.TransactionId.Should().Be("tx-8821", "the card still holds the operator's selection");
        window.StillOpenRowTexts.Should().HaveCount(linesBefore - 1, "a proven payment leaves the strip");
        window.StillOpenRowTexts[0].Should().Contain("…4567");
    }


    [Fact]
    public async Task ArrivingEvent_LeavesTheListScrolledExactlyWhereTheOperatorLeftIt()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);
        // Comfortably more open payments than the strip has rows, so the one that resolves
        // cannot be mistaken for the clamp that keeps a shrinking list inside its own content.
        for (var index = 0; index < 24; index++)
        {
            harness.Payments.Queue(new PaymentResult(
                PaymentOutcome.Pending, 202, "payment-id", $"tx-{index}", "Pending", "{}", null, TimeSpan.Zero));
            await controller.SubmitPaymentAsync(
                new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Standard),
                IdempotencyMode.Generated,
                null,
                CancellationToken.None);
        }

        window.RenderForTest();
        // The operator scrolled down to watch a specific row.
        window.ScrollStillOpenListForTest(6);
        var offsetBefore = window.StillOpenList.Viewport.Location.Y;
        offsetBefore.Should().BeGreaterThan(0, "the list must actually be scrolled for this to prove anything");

        harness.Feed.PushCompleted("tx-0", new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        window.RenderForTest();

        window.StillOpenList.Viewport.Location.Y.Should().Be(
            offsetBefore,
            "a list that scrolled itself under a live demonstration is a stage failure");
    }

    [Fact]
    public async Task SelectingAnEvidenceRow_MovesTheDetailPaneToThatRecord()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "payment-id", "tx-8821", "Pending",
            "{\"transactionId\":\"tx-8821\"}", null, TimeSpan.FromMilliseconds(5)));
        await controller.SubmitPaymentAsync(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Generated,
            null,
            CancellationToken.None);
        using var window = CreateWindow(controller);
        window.RenderForTest();
        window.EvidenceRowCount.Should().BeGreaterThan(1, "the attach and the payment are both recorded");

        // Move off the newest row, which is what auto-selection already had.
        window.EvidenceList.SelectedItem = window.EvidenceRowCount - 1;
        window.RenderForTest();

        var oldest = controller.State.Evidence.OrderBy(record => record.Sequence).First();
        controller.State.SelectedEvidence!.Sequence.Should().Be(
            oldest.Sequence,
            "moving through the list is how the journal is read; the pane follows the selection");
        window.EvidenceDetailText.Should().Contain(oldest.Summary);
    }

    [Fact]
    public async Task RenderingAfterAnArrivingEvent_DoesNotPullTheDetailPaneOffTheOperatorsChoice()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "payment-id", "tx-8821", "Pending", "{}", null, TimeSpan.FromMilliseconds(5)));
        await controller.SubmitPaymentAsync(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Generated,
            null,
            CancellationToken.None);
        using var window = CreateWindow(controller);
        window.RenderForTest();
        window.EvidenceList.SelectedItem = window.EvidenceRowCount - 1;
        window.RenderForTest();
        var chosen = controller.State.SelectedEvidence!.Sequence;

        // A rebind restores the selection; reacting to that would call back into the controller
        // and drag the pane onto whatever the rebind happened to land on.
        harness.Feed.PushCompleted("tx-8821", new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        window.RenderForTest();

        controller.State.SelectedEvidence!.Sequence.Should().Be(chosen);
    }

    /// <summary>
    /// The palette toggle is reachable from every workspace, like panic-off, because a
    /// projector that washes the dark canvas out is discovered mid-talk, not before it.
    /// </summary>
    [Fact]
    public void ShortcutT_TogglesThePaletteFromAnyWorkspace()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        var started = OperatorTheme.Mode;

        window.HandleKeyForTest(Key.T).Should().BeTrue();
        OperatorTheme.Mode.Should().NotBe(started);

        window.HandleKeyForTest(Key.T).Should().BeTrue();
        OperatorTheme.Mode.Should().Be(started);
    }

    /// <summary>
    /// Toggling is a redraw, not a navigation: it must not move the operator off the
    /// workspace they were presenting from.
    /// </summary>
    [Fact]
    public void ShortcutT_DoesNotChangeTheActiveWorkspace()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        window.HandleKeyForTest(Key.D4);

        window.HandleKeyForTest(Key.T);

        controller.State.ActiveWorkspace.Should().Be(WorkspaceKind.LoadTest);
        window.IsWorkspaceVisible(WorkspaceKind.LoadTest).Should().BeTrue();
        OperatorTheme.Register(ThemeMode.Dark);
    }

    /// <summary>
    /// End-to-end wiring, not just the scheme table: a window constructed in light mode must
    /// have its actual views resolve to the light canvas. Views hold only a scheme *name* and
    /// resolve it through SchemeManager at draw time, which is what lets the T toggle repaint
    /// without walking the view tree - so this also pins the mechanism the toggle depends on.
    /// </summary>
    [Theory]
    [InlineData(ThemeMode.Dark, false)]
    [InlineData(ThemeMode.Light, true)]
    public void Window_ResolvesTheRequestedPalette(ThemeMode mode, bool expectLightCanvas)
    {
        var controller = new OperatorHarness().CreateController();
        using var window = new MainWindow(
            controller,
            () => Task.CompletedTask,
            new FakeConfirmationService(),
            startPolling: false,
            marshalUpdates: false,
            theme: mode);

        var canvas = window.GetScheme().Normal.Background;
        var isLight = canvas.R > 200 && canvas.G > 200 && canvas.B > 200;

        isLight.Should().Be(expectLightCanvas);
        OperatorTheme.Register(ThemeMode.Dark);
    }

    /// <summary>
    /// Removing the three-row bottom band and collapsing the sixteen-row form re-cuts the rows at
    /// 100x30 from 10 of shell chrome and 4 of payment area to 7 and 20.
    /// </summary>
    [Fact]
    public void OperationsRowBudget_At100x30_Spends7RowsOnChromeAnd20OnThePaymentArea()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);

        window.ResizeForTest(100, 30);
        window.RenderForTest();

        window.OperationsChromeRowCount.Should().Be(7);
        window.OperationsPaymentAreaRows.Should().Be(20);
    }

    /// <summary>
    /// The rail is the longest of the five labels plus its border and not one more, so every
    /// workspace gets six more columns back.
    /// </summary>
    [Fact]
    public void NavigationRail_AtThePreferredWidth_Is16Columns()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);

        window.ResizeForTest(100, 30);

        window.NavigationFrameWidth.Should().Be(16);
    }

    /// <summary>
    /// Every rail row is one line: marker, shortcut digit and label together, inside the width
    /// the rail actually has. It shipped wrapping — Terminal.Gui brackets and pads a Button's
    /// title by default, four cells the 16-column derivation never budgeted for, so "▸4 Load
    /// Test" reflowed into "4 Load" / "Test" and read as two workspaces. Asserting the label
    /// fits its own laid-out frame is what catches that, and it catches the next label that
    /// outgrows the rail too — a rail row is where a long name is least survivable.
    /// </summary>
    [Fact]
    public void NavigationLabels_EachFitOneRowAtThePreferredWidth()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);

        window.ResizeForTest(100, 30);
        window.RenderForTest();

        foreach (var button in window.NavigationButtons)
        {
            // Two things here are easy to get wrong and both shipped once. TextFormatter.Text is
            // what actually reaches the screen, decorations included, so asserting Button.Text
            // would measure the string we handed in and miss the four cells that caused the wrap.
            // And the row's usable width is its *Viewport*, not its Frame: a Button reserves a
            // one-cell margin for its drop shadow, so Frame overstates the room by one.
            var drawn = button.TextFormatter.Text;
            drawn.GetColumns().Should().BeLessThanOrEqualTo(
                button.Viewport.Width,
                "'{0}' as drawn must fit the {1} cells the rail actually leaves it",
                drawn,
                button.Viewport.Width);
        }

        window.NavigationButtons.Select(button => button.Text.Trim().Split(' ')).Should().AllSatisfy(
            tokens => tokens.Should().HaveCount(
                2,
                "a rail row carries exactly two tokens — the marked shortcut and a one-word label"));
    }

    /// <summary>
    /// A rail row is a control, and it has to look like one. The bracket glyphs a Terminal.Gui
    /// Button draws by default cannot fit the 16-column rail, so the active row carries an
    /// accent-navy fill instead and the inactive rows carry the rail tone. The marker is what
    /// actually encodes the active state — the fill is reinforcement that survives neither being
    /// read in monochrome nor being the only signal — so both are asserted here: without the
    /// scheme the whole rail renders as one flat block of text with nothing to press.
    /// </summary>
    [Fact]
    public void NavigationRail_MarksTheActiveWorkspaceByBothMarkerAndFill()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);

        window.HandleKeyForTest(Key.D4);
        window.RenderForTest();

        var buttons = window.NavigationButtons;
        buttons[3].SchemeName.Should().Be(
            OperatorTheme.NavigationActiveScheme, "the active workspace's row is filled");
        buttons[3].Text.Should().StartWith("▸", "the marker, not the fill, carries the active state");
        buttons.Where((_, index) => index != 3).Should().AllSatisfy(button =>
        {
            button.SchemeName.Should().Be(OperatorTheme.RailScheme);
            button.Text.Should().NotStartWith("▸");
        });

        window.HandleKeyForTest(Key.D1);
        window.RenderForTest();

        buttons[0].SchemeName.Should().Be(OperatorTheme.NavigationActiveScheme, "the fill moves with the selection");
        buttons[3].SchemeName.Should().Be(OperatorTheme.RailScheme);
    }

    /// <summary>
    /// The card is budgeted first and everything else is compressed into what is left. At every
    /// supported size its state word, its clock and its action are on screen, and so is the feed
    /// statement in one of its two forms.
    /// </summary>
    [Theory]
    [InlineData(80, 24)]
    [InlineData(100, 30)]
    [InlineData(120, 40)]
    public void FocusCard_KeepsItsStateWordClockAndAction_AtEverySupportedTerminalSize(int width, int height)
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);

        window.ResizeForTest(width, height);
        window.RenderForTest();

        var workspaceHeight = window.CardStateLabel.SuperView!.Viewport.Height;
        window.CardStateLabel.Frame.Y.Should().BeGreaterThan(
            window.ComposeRuleLabel.Frame.Y,
            "the card sits below the compose bar's rule at {0}x{1}",
            width,
            height);
        window.CardStateLabel.Frame.Y.Should().BeLessThan(
            workspaceHeight,
            "the card's state line is on screen at {0}x{1}",
            width,
            height);
        window.CardActionButton.Frame.Y.Should().Be(window.CardStateLabel.Frame.Y);
        window.FeedStatusText.Should().NotBeEmpty("the feed statement is surrendered at no width");
    }

    /// <summary>
    /// The compose bar's third line belongs to the mode that needs it, and to no other: the
    /// supplied-key field in Supplied mode, the not-retry-safe warning in Omitted mode.
    /// </summary>
    [Fact]
    public void ComposeBarThirdLine_AppearsOnlyInTheModeThatNeedsIt()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        window.ResizeForTest(120, 40);

        window.SetIdempotencyModeForTest(IdempotencyMode.Generated);
        window.ModeLineVisible.Should().BeFalse();

        window.SetIdempotencyModeForTest(IdempotencyMode.Supplied);
        window.ModeLineVisible.Should().BeTrue();
        window.ModeLineText.Should().Be("Supplied key");

        window.SetIdempotencyModeForTest(IdempotencyMode.Omitted);
        window.ModeLineVisible.Should().BeTrue();
        window.ModeLineText.Should().Contain("not retry-safe");
    }

    /// <summary>
    /// R and Q were the removed StatusBar's only home. Keyboard parity is a floor: every action
    /// reachable by mouse has a keyboard path, so both had to survive the band's removal.
    /// </summary>
    [Fact]
    public void RefreshAndQuit_SurviveTheRemovedStatusBarAsWindowWideKeys()
    {
        var harness = new OperatorHarness();
        var controller = harness.CreateController();
        var exited = false;
        using var window = new MainWindow(
            controller,
            () => { exited = true; return Task.CompletedTask; },
            null,
            startPolling: false,
            marshalUpdates: false);

        window.HandleKeyForTest(Key.R).Should().BeTrue();
        window.HandleKeyForTest(Key.Q).Should().BeTrue();

        exited.Should().BeTrue("Q is the only way out and it left with the status bar");
    }

    /// <summary>
    /// The rail's Quit button is the mouse path to the same exit Shift+Q reaches by keyboard,
    /// so clicking it must terminate the application exactly as reliably as the key does.
    /// </summary>
    [Fact]
    public void QuitButton_ClickAlwaysTerminatesTheApplication()
    {
        var harness = new OperatorHarness();
        var controller = harness.CreateController();
        var exited = false;
        using var window = new MainWindow(
            controller,
            () => { exited = true; return Task.CompletedTask; },
            null,
            startPolling: false,
            marshalUpdates: false);

        window.QuitButton.InvokeCommand(Command.Accept);

        exited.Should().BeTrue("the rail's Quit button is the mouse path to Shift+Q's exit");
    }

    /// <summary>
    /// The card's own action, fired with no confirmation modal: a cancellation destroys no state.
    /// </summary>
    [Fact]
    public async Task CardAction_OnAnOpenPayment_DispatchesACancelWithNoConfirmation()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        var confirmation = new FakeConfirmationService { Result = false };
        using var window = CreateWindow(controller, confirmation);
        window.ResizeForTest(100, 30);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "payment-id", "tx-8821", "Pending", "{}", null, TimeSpan.Zero));
        await controller.SubmitPaymentAsync(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Instant),
            IdempotencyMode.Supplied,
            "tx-8821",
            CancellationToken.None);
        window.RenderForTest();
        window.FocusCard.ActionLabel.Should().Be(CardActions.Cancel);

        window.TriggerCardActionForTest();
        await window.LastDispatchedTask!;
        window.RenderForTest();

        confirmation.Requests.Should().BeEmpty("Cancel payment fires immediately — no modal, no Y");
        harness.Payments.Cancellations.Should().ContainSingle(request => request.TransactionId == "tx-8821");
        window.FocusCard.StateWord.Should().Be("CANCELLED");
        window.StillOpenVisible.Should().BeFalse("a proven cancellation leaves the strip");
    }

    /// <summary>
    /// A running burst replaces the compose bar, the focus card and the strip rather than
    /// rendering as one more object among them.
    /// </summary>
    [Fact]
    public async Task RunningBurst_TakesTheWholeWorkspaceOverAndHoldsItsResult()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);
        window.BurstCountField.Text = "2";
        window.BurstConcurrencyField.Text = "1";

        await window.TriggerBurstForTestAsync();
        window.RenderForTest();

        window.BurstTakeoverVisible.Should().BeTrue(
            "on drain the takeover holds its final summary until the operator dismisses it");
        // The two counter lines are never merged: a burst is exactly where "acknowledged" and
        // "finished" diverge, and `still moving` draining to zero is the confirmation.
        window.BurstStatusText.Should().StartWith("Sent").And.Contain("2 / 2");
        window.BurstProvenStatusText.Should().StartWith("Settled").And.Contain("still moving 2");
        window.BurstClosingText.Should().BeEmpty(
            "two payments are still moving, so nothing may claim the burst proved itself");

        foreach (var submission in harness.Payments.Submissions)
        {
            harness.Feed.PushCompleted(submission.IdempotencyKey!, new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        }

        window.RenderForTest();

        window.BurstProvenStatusText.Should().Contain("still moving 0");
        window.BurstClosingText.Should().Be("every payment proved itself · nothing left awaiting");
    }

    /// <summary>
    /// Currency is not an operator input: there is no field on screen and no currency validation
    /// rule, and the console always sends EUR (brief §6).
    /// </summary>
    [Fact]
    public async Task ComposeBarSubmit_AlwaysSendsEurAndOffersNoCurrencyInput()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);
        window.AmountField.Text = "250.00";

        await window.TriggerSubmitForTestAsync();
        window.BurstCountField.Text = "2";
        window.BurstConcurrencyField.Text = "1";
        await window.TriggerBurstForTestAsync();

        harness.Payments.Submissions.Should().OnlyContain(submission => submission.Request.Currency == "EUR");
    }

    /// <summary>
    /// The burst Cancel and the Faults controls remain the only lock exemptions. Cancel payment
    /// is exempt from confirmation, never from the lock, so it must not borrow the outline that
    /// means "still live while everything else is dimmed" — nor the destructive red, which is
    /// this console's one data-loss warning and a cancellation loses no data.
    /// </summary>
    [Fact]
    public void CardAction_WearsNeitherTheLockExemptOutlineNorTheDestructiveTreatment()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        window.RenderForTest();

        window.CardActionButton.SchemeName.Should().NotBe(OperatorTheme.LockExemptScheme);
        window.CardActionButton.SchemeName.Should().NotBe(OperatorTheme.DestructiveScheme);
        window.CardActionButton.SchemeName.Should().NotBe(
            OperatorTheme.ActionScheme,
            "Submit is Operations' single filled-teal control");
    }

    /// <summary>
    /// Nothing is ever hidden: a surplus the strip has no rows for is stated on its rule as an
    /// explicit count, exactly as the Evidence feed states its elided rows.
    /// </summary>
    [Fact]
    public async Task StillOpenStrip_WithMoreOpenPaymentsThanRows_StatesTheSurplusRatherThanDroppingIt()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.ResizeForTest(80, 24);
        for (var index = 0; index < 12; index++)
        {
            harness.Payments.Queue(new PaymentResult(
                PaymentOutcome.Pending, 202, "payment-id", $"tx-{index}", "Pending", "{}", null, TimeSpan.Zero));
            await controller.SubmitPaymentAsync(
                new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Standard),
                IdempotencyMode.Supplied,
                $"tx-{index}",
                CancellationToken.None);
        }

        window.RenderForTest();

        window.StillOpenVisible.Should().BeTrue();
        window.StillOpenVisibleRows.Should().BeLessThan(12, "the 80x24 floor cannot show every open payment");
        window.StillOpenVisibleRows.Should().BeGreaterThanOrEqualTo(
            2,
            "the strip never falls below two rows while more than one payment is open");
        window.StillOpenRuleText.Should().Contain("more open").And.Contain("STILL OPEN");
        window.StillOpenRuleText.Should().Contain(
            "Listening since",
            "the region's feed statement is surrendered at no width");
    }

    /// <summary>
    /// At the floor the compose bar keeps both captions and every control: where the chips and
    /// Burst… cannot share the second line, the action wraps to a third rather than shedding a
    /// caption. An unlabelled IBAN read from the back of a room is a run of digits, so a caption
    /// is worth more than the row it costs, and no control in use is ever hidden.
    /// </summary>
    [Theory]
    [InlineData(80, 24, 2)]
    [InlineData(100, 30, 1)]
    public void ComposeBar_KeepsBothCaptionsAndEveryControl_WrappingBurstRatherThanHidingIt(
        int width,
        int height,
        int expectedBurstRow)
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);

        window.ResizeForTest(width, height);
        window.RenderForTest();

        window.FromAccountField.Visible.Should().BeTrue();
        window.ToAccountField.Visible.Should().BeTrue();
        window.AmountField.Visible.Should().BeTrue();
        window.RailButton.Visible.Should().BeTrue();
        window.IdempotencyButton.Visible.Should().BeTrue();
        window.SubmitButton.Frame.Y.Should().Be(0);
        window.BurstButton.Frame.Y.Should().Be(expectedBurstRow);
        if (expectedBurstRow == window.IdempotencyButton.Frame.Y)
        {
            window.BurstButton.Frame.X.Should().BeGreaterThan(
                window.IdempotencyButton.Frame.X + window.IdempotencyButton.Frame.Width - 1,
                "Burst… never overlaps the chip beside it at {0}x{1}",
                width,
                height);
        }
        window.ComposeRuleLabel.Frame.Y.Should().BeGreaterThan(
            window.BurstButton.Frame.Y,
            "the rule closes the bar beneath every line it grew");
        window.FocusCard.StateWord.Should().NotBeEmpty("the state word is never abbreviated to fit");
    }

    /// <summary>
    /// The announcement inherited the removed band's job without inheriting its rows, and the
    /// band was in every workspace. A refusal appears at the foot of whichever workspace is
    /// active, where the operator is already looking — a refusal that could only be read in
    /// Operations would leave the announcement carrying no copy at all of what it said.
    /// </summary>
    [Fact]
    public async Task Announcement_IsReadableInEveryWorkspace_AndReservesNoRowWhenSilent()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);
        window.RenderForTest();

        foreach (var workspace in Enum.GetValues<WorkspaceKind>())
        {
            window.AnnouncementVisibleIn(workspace).Should().BeFalse(
                "an empty foot is the resting state in {0}",
                workspace);
        }

        // A refusal the console produced itself, from the Resources workspace.
        await window.TriggerResendForTestAsync();
        window.RenderForTest();

        foreach (var workspace in Enum.GetValues<WorkspaceKind>())
        {
            window.AnnouncementVisibleIn(workspace).Should().BeTrue(
                "the same line is readable at the foot of {0}",
                workspace);
        }

        window.AnnouncementLineText.Should().StartWith("✕").And.Contain("No retry-safe");
        window.AnnouncementIsFailureToned.Should().BeTrue();
    }

    /// <summary>
    /// A notice that is no verdict at all takes the neutral tone: the largest single-line
    /// statement on the screen must never assert a failure that nothing has proved.
    /// </summary>
    [Fact]
    public void Announcement_TakesTheToneOfTheThingItAnnounces()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);

        window.HandleKeyForTest(Key.T);
        window.RenderForTest();

        window.AnnouncementLineText.Should().StartWith("○").And.Contain("Theme:");
        window.AnnouncementIsFailureToned.Should().BeFalse("a palette switch proves nothing about anything");
    }

    /// <summary>
    /// The takeover replaces every Operations surface, the announcement's row included, so it
    /// carries its own rather than letting a refusal vanish behind the counters.
    /// </summary>
    [Fact]
    public async Task Announcement_SurvivesTheBurstTakeover()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);
        window.BurstCountField.Text = "2";
        window.BurstConcurrencyField.Text = "1";
        await window.TriggerBurstForTestAsync();
        window.RenderForTest();
        window.BurstTakeoverVisible.Should().BeTrue();

        window.CancelBurstButton.InvokeCommand(Command.Accept);
        window.RenderForTest();

        window.AnnouncementVisibleIn(WorkspaceKind.Operations).Should().BeTrue(
            "a refusal fired from the takeover has to be readable on it");
        window.AnnouncementLineText.Should().Contain("No active burst");
    }

    /// <summary>
    /// The takeover holds its result until dismissed, and Done is the only way back. If it were
    /// ever unwired, Operations would be stuck on the summary with no route to the compose bar.
    /// </summary>
    [Fact]
    public async Task BurstTakeover_Done_ReturnsTheWorkspaceToTheComposeBarAndCard()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);
        window.BurstCountField.Text = "2";
        window.BurstConcurrencyField.Text = "1";

        await window.TriggerBurstForTestAsync();
        window.RenderForTest();
        window.BurstTakeoverVisible.Should().BeTrue("a drained burst holds its summary until dismissed");

        window.BurstDismissButton.InvokeCommand(Command.Accept);

        window.BurstTakeoverVisible.Should().BeFalse();
        window.CardStateLabel.Visible.Should().BeTrue("the compose bar and the card come back");
    }

    /// <summary>
    /// Resources answers "is it running?", which is the question every sentence on the console's
    /// own topology status line is about. The removed bottom band used to carry it in all five
    /// workspaces; losing it entirely would have cost the session a fact.
    /// </summary>
    [Fact]
    public async Task TopologyStatusLine_IsReadableInResources()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        controller.SelectWorkspace(WorkspaceKind.Resources);
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);
        window.RenderForTest();

        window.TopologyStatusText.Should().Be(controller.State.StatusLine).And.NotBeEmpty();
    }

    /// <summary>
    /// The card is budgeted first and everything else is compressed into what is left. This
    /// asserts real laid-out frames rather than arithmetic on the constants that produced them:
    /// the predecessor of this test caught a payment area squeezed to zero rows at two of the
    /// three supported sizes, which constant arithmetic agreed with.
    /// </summary>
    [Theory]
    [InlineData(80, 24)]
    [InlineData(100, 30)]
    [InlineData(120, 40)]
    public async Task OperationsRegions_StayOnScreenAndNeverOverlap_AtEverySupportedTerminalSize(
        int width,
        int height)
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.ResizeForTest(width, height);
        foreach (var id in new[] { "tx-1", "tx-2", "tx-3" })
        {
            harness.Payments.Queue(new PaymentResult(
                PaymentOutcome.Pending, 202, "payment-id", id, "Pending", "{}", null, TimeSpan.Zero));
            await controller.SubmitPaymentAsync(
                new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Standard),
                IdempotencyMode.Supplied,
                id,
                CancellationToken.None);
        }

        window.RenderForTest();

        var inner = window.CardStateLabel.SuperView!.Viewport.Height;
        var meta = window.CardMetaLabel.Frame;
        var closing = window.CardClosingLabel.Frame;
        var rule = window.StillOpenRuleLabel.Frame;
        var strip = window.StillOpenList.Frame;

        window.StillOpenVisible.Should().BeTrue("three payments are open at {0}x{1}", width, height);
        (meta.Y + meta.Height).Should().BeLessThanOrEqualTo(
            inner, "the card's meta line is on screen at {0}x{1}", width, height);
        strip.Height.Should().BeGreaterThanOrEqualTo(
            2, "the strip never falls below two rows while more than one payment is open");
        (strip.Y + strip.Height).Should().BeLessThanOrEqualTo(
            inner, "the strip stays inside the workspace at {0}x{1}", width, height);
        rule.Y.Should().Be(strip.Y - 1, "the captioned rule opens the strip");
        meta.Y.Should().BeLessThan(rule.Y, "the card's grid never runs under the strip");
        if (window.CardClosingLabel.Visible)
        {
            (closing.Y + closing.Height).Should().BeLessThanOrEqualTo(
                rule.Y, "the closing block yields its rows to the strip rather than overlapping it");
        }
    }

    // --- The Details pane's three controls -------------------------------------------------

    [Fact]
    public async Task EvidencePane_Exchange_FillsBothColumnsAndCopiesTheRawText()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending,
            202,
            "payment-id",
            "tx-8821",
            "Pending",
            """{"transactionId":"tx-8821"}""",
            null,
            TimeSpan.FromMilliseconds(5),
            new HttpExchange(
                "POST",
                "http://127.0.0.1:5294/api/payments",
                [new EvidenceHeader("Idempotency-Key", "demo-key-001")],
                """{"amount":250}""",
                202,
                "Accepted",
                [new EvidenceHeader("Content-Type", "application/json")],
                """{"transactionId":"tx-8821"}""")));
        await controller.SubmitPaymentAsync(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Generated,
            null,
            CancellationToken.None);
        using var window = CreateWindow(controller);
        var terminal = new StringWriter();
        window.TerminalOut = terminal;
        window.HandleKeyForTest(Key.D3);
        window.ResizeForTest(120, 40);
        window.RenderForTest();
        window.Layout();

        // The key and the id the audience is asked to compare are on screen together.
        window.EvidenceRequestText.Should().StartWith("REQUEST").And.Contain("Idempotency-Key: demo-key-001");
        window.EvidenceResponseText.Should().StartWith("RESPONSE").And.Contain("tx-8821");
        window.EvidenceResponseVisible.Should().BeTrue();
        window.EvidenceColumnWidths.Left.Should().BeGreaterThan(0);
        window.EvidenceColumnWidths.Right.Should().BeGreaterThan(0);

        // And neither column is clipped away at the 80x24 floor: scroll bars cover the
        // degradation, which is why there is no narrow-terminal layout fallback to maintain.
        window.ResizeForTest(80, 24);
        window.RenderForTest();
        window.Layout();
        window.EvidenceColumnWidths.Left.Should().BeGreaterThan(0);
        window.EvidenceColumnWidths.Right.Should().BeGreaterThan(0);
        window.ResizeForTest(120, 40);

        window.CopyButton.InvokeCommand(Command.Accept);

        terminal.ToString().Should().StartWith("\u001b]52;c;");
        window.EvidenceCopyText.Should().StartWith(window.EvidenceHeaderText)
            .And.Contain("POST http://127.0.0.1:5294/api/payments")
            .And.NotContain("REQUEST")
            .And.NotContain("RESPONSE");
    }

    [Fact]
    public async Task EvidencePane_SelectingAnExchangeAfterAPayloadLessRecord_BringsTheResponseColumnBack()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending,
            202,
            "payment-id",
            "tx-8821",
            "Pending",
            """{"transactionId":"tx-8821"}""",
            null,
            TimeSpan.FromMilliseconds(5),
            new HttpExchange(
                "POST",
                "http://127.0.0.1:5294/api/payments",
                [new EvidenceHeader("Idempotency-Key", "demo-key-001")],
                """{"amount":250}""",
                202,
                "Accepted",
                [new EvidenceHeader("Content-Type", "application/json")],
                """{"transactionId":"tx-8821"}""")));
        await controller.SubmitPaymentAsync(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Generated,
            null,
            CancellationToken.None);
        using var window = CreateWindow(controller);
        window.HandleKeyForTest(Key.D3);
        window.ResizeForTest(120, 40);

        // The attach record: one column, no second.
        var attach = controller.State.Evidence.OrderBy(record => record.Sequence).First();
        controller.SelectEvidence(attach.Sequence);
        window.RenderForTest();
        window.Layout();
        window.EvidenceResponseVisible.Should().BeFalse();

        // And back again: a column that hides and never returns is the failure this covers.
        var payment = controller.State.Evidence.Single(record => record.Exchange is not null);
        controller.SelectEvidence(payment.Sequence);
        window.RenderForTest();
        window.Layout();

        window.EvidenceResponseVisible.Should().BeTrue();
        window.EvidenceColumnWidths.Right.Should().BeGreaterThan(0);
        window.EvidenceResponseText.Should().Contain("tx-8821");
    }

    [Fact]
    public async Task EvidencePane_RecordWithNoSecondColumn_HidesItRatherThanShowingItEmpty()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();

        // An attach is not an HTTP exchange, so its record has one column and no second.
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.ResizeForTest(120, 40);
        window.RenderForTest();

        window.EvidenceResponseVisible.Should().BeFalse();
        window.EvidenceRequestText.Should().Contain("was not an HTTP exchange");
    }

    [Fact]
    public void EvidencePane_TheSameKeyTwice_RendersTwoByteIdenticalResponseColumns()
    {
        // The retry beat: two records side by side, and the room sees the bytes are the same
        // without being told. Nothing in the projection may vary between two equal exchanges.
        // Two separately built exchanges, as two captures of the same idempotent answer would
        // be. Handing one object to both records would only prove the projection is a function.
        var first = EvidenceForTest(31, ReplayedExchange());
        var second = EvidenceForTest(32, ReplayedExchange());
        first.Exchange.Should().NotBeSameAs(second.Exchange);

        PresentationModelBuilder.EvidencePane(first).Right
            .Should().Be(PresentationModelBuilder.EvidencePane(second).Right);
    }

    private static HttpExchange ReplayedExchange() => new(
        "POST",
        "http://127.0.0.1:5294/api/payments",
        [new EvidenceHeader("Idempotency-Key", "demo-key-001")],
        """{"amount":250}""",
        202,
        "Accepted",
        [new EvidenceHeader("Content-Type", "application/json")],
        """{"paymentId":"p1","transactionId":"demo-key-001","status":"Pending"}""");

    private static EvidenceRecord EvidenceForTest(long sequence, HttpExchange exchange) => new(
        sequence,
        DateTimeOffset.UnixEpoch,
        TopologyProfile.Regular,
        1,
        EvidenceKind.Payment,
        "202 Pending",
        "POST",
        "payments.submit",
        202,
        TimeSpan.FromMilliseconds(12),
        string.Empty,
        true,
        TransactionId: "demo-key-001",
        Exchange: exchange);

    [Fact]
    public void EvidencePane_WrapToggle_AppliesToBothColumns()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        window.RenderForTest();

        window.WrapButton.InvokeCommand(Command.Accept);

        window.WrapButton.Text.Should().Be("Wrap: on");
        window.EvidenceColumnsWrap.Should().BeTrue(
            "a toggle that moved one pane and not the others reads as a bug, and the header block "
            + "holds the full summary that row truncation exists to accommodate");
    }

    [Fact]
    public void EvidencePane_Border_FollowsTheNavigationRailBetweenLayouts()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);

        window.HandleKeyForTest(Key.D3);
        window.ResizeForTest(120, 40);
        window.RenderForTest();
        window.Layout();
        window.EvidenceDetailBorderStyle.Should().Be(LineStyle.Rounded);

        // At the 80x24 floor the border goes the way the rail's does.
        window.ResizeForTest(80, 24);
        window.RenderForTest();
        window.Layout();
        window.EvidenceDetailBorderStyle.Should().Be(LineStyle.None);
    }

    [Theory]
    [InlineData(120, 40)]
    [InlineData(80, 24)]
    public void EvidencePane_FirstTextRow_LinesUpWithTheListBesideIt(int width, int height)
    {
        // The border is an adornment: it takes a row and a column off every side of the
        // container while the list beside it has none, so without compensating for its
        // thickness the two panes read one row and one column out of step.
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        window.HandleKeyForTest(Key.D3);
        window.ResizeForTest(width, height);
        window.RenderForTest();
        window.Layout();

        var (list, header) = window.EvidenceFirstRows;
        header.Y.Should().Be(list.Y, "the two panes' first text rows are one row");
        header.X.Should().BeGreaterThan(list.X, "the pane sits to the right of the list");
    }

    /// <summary>Display-only reset: no backend call, matching <see cref="OperatorConsoleController.ClearTrackedPayments"/>.</summary>
    [Fact]
    public async Task ClearButton_EmptiesTheStillOpenStripAndTheSelection()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "payment-id", "tx-clear", "Pending", "{}", null, TimeSpan.FromMilliseconds(5)));
        await controller.SubmitPaymentAsync(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Generated,
            null,
            CancellationToken.None);
        window.RenderForTest();
        controller.State.TrackedPayments.Should().NotBeEmpty();

        window.ClearTrackedPaymentsButton.InvokeCommand(Command.Accept);

        controller.State.TrackedPayments.Should().BeEmpty();
        controller.State.SelectedPayment.Should().BeNull();
        window.RenderForTest();
        window.StillOpenVisible.Should().BeFalse("the strip has nothing left to list");
    }

    /// <summary>Display-only reset: no backend call, matching <see cref="OperatorConsoleController.ClearEvidence"/>.</summary>
    [Fact]
    public async Task ClearButton_EmptiesTheEvidenceLog()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.LoadTests));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.LoadTests, CancellationToken.None);
        using var window = CreateWindow(controller);
        await controller.QueryOutcomeAsync("known-id", CancellationToken.None);
        window.RenderForTest();
        controller.State.Evidence.Should().NotBeEmpty();

        window.ClearEvidenceButton.InvokeCommand(Command.Accept);

        controller.State.Evidence.Should().BeEmpty();
        controller.State.SelectedEvidence.Should().BeNull();
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
