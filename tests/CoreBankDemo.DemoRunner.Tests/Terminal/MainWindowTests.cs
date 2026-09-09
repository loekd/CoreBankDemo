using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Infrastructure;
using CoreBankDemo.DemoRunner.Terminal;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Terminal.Gui.Input;
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
        for (var index = 0; index < 12; index++)
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
        window.BurstClosingText.Should().BeEmpty(
            "two payments are still moving, so nothing may claim the burst proved itself");

        foreach (var submission in harness.Payments.Submissions)
        {
            harness.Feed.PushCompleted(submission.IdempotencyKey!, new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        }

        window.RenderForTest();

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
