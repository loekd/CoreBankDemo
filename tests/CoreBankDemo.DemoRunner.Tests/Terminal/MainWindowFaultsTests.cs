using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Terminal;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Terminal.Gui.Input;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

public class MainWindowFaultsTests
{
    private static readonly PaymentRequest StandardPayment =
        new("NL91ABNA0417164300", "NL20INGB0001234567", 10m, "EUR", PaymentRail.Standard);

    [Fact]
    public async Task FaultControlsStayEnabledWhileABurstHoldsTheLock()
    {
        var harness = ArmedHarness();
        var controller = harness.CreateController();
        using var window = CreateWindow(controller);
        await ArmAsync(controller);
        harness.Payments.ReleaseSubmission = new TaskCompletionSource();
        harness.Payments.SubmissionStarted = new TaskCompletionSource();
        var burst = controller.RunBurstAsync(StandardPayment, 3, 1, CancellationToken.None);
        await harness.Payments.SubmissionStarted.Task;

        window.HandleKeyForTest(Key.D5);
        window.RenderForTest();

        window.IsWorkspaceVisible(WorkspaceKind.Faults).Should().BeTrue("the workspace opens while the burst runs");
        window.FaultKnobsEnabled.Should().AllBeEquivalentTo(true);
        window.PanicOffButton.Enabled.Should().BeTrue();
        window.SubmitButton.Enabled.Should().BeFalse("every other mutating control is dimmed");

        harness.Payments.ReleaseSubmission.SetResult();
        await burst;
    }

    [Fact]
    public async Task StagingThroughAPresetEnablesApplyAndShowsTheDelta()
    {
        var harness = ArmedHarness();
        var controller = harness.CreateController();
        using var window = CreateWindow(controller);
        await ArmAsync(controller);
        window.RenderForTest();

        window.VisiblePresetNames.Should().Equal("All off", "Regular profile");
        window.TriggerPresetForTest(1);
        window.RenderForTest();

        window.ApplyFaultsButton.Enabled.Should().BeTrue();
        window.ApplyFaultsButton.Text.Should().Contain("staged");
        window.FaultValueTexts[0].Should().Be("0% → 5%");
        window.PresetLabelText.Should().Contain("Regular profile");
        harness.Faults.Writes.Should().BeEmpty("staging never writes");

        await window.TriggerApplyFaultsForTestAsync();
        window.RenderForTest();

        harness.Faults.Writes.Should().ContainSingle()
            .Which.Levels.Should().Be(FaultLevels.CheckedInDefaults(TopologyProfile.Regular));
        window.FaultValueTexts[0].Should().Be("5%");
        window.ApplyFaultsButton.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task ZeroKeyPanicsOffFromAnyWorkspaceWithoutAConfirmation()
    {
        var harness = ArmedHarness();
        var controller = harness.CreateController();
        using var window = CreateWindow(controller, new ThrowingConfirmationService());
        await ArmAsync(controller);
        window.TriggerPresetForTest(1);
        await window.TriggerApplyFaultsForTestAsync();
        controller.SelectWorkspace(WorkspaceKind.Operations);
        harness.Faults.Writes.Clear();

        window.HandleKeyForTest(Key.D0).Should().BeTrue();
        await window.LastDispatchedTask!;

        harness.Faults.Writes.Should().ContainSingle().Which.Levels.Should().Be(FaultLevels.AllZero);
        controller.State.Applied.IsAllZero.Should().BeTrue();
        controller.State.ActiveWorkspace.Should().Be(WorkspaceKind.Operations, "panic-off never navigates");
    }

    [Fact]
    public async Task EveryKnobIsFullyOperableFromTheKeyboardAndOnlyEverStages()
    {
        var harness = ArmedHarness();
        var controller = harness.CreateController();
        using var window = CreateWindow(controller);
        await ArmAsync(controller);
        window.RenderForTest();

        window.SendFaultKnobKeyForTest(0, Key.CursorRight).Should().BeTrue();
        window.RenderForTest();
        controller.State.Staged.ErrorRatePercent.Should().Be(5);
        window.FaultValueTexts[0].Should().Be("0% → 5%");

        window.SendFaultKnobKeyForTest(0, Key.CursorRight.WithShift);
        window.RenderForTest();
        controller.State.Staged.ErrorRatePercent.Should().Be(40, "Shift+arrow is the coarse step");

        window.SendFaultKnobKeyForTest(0, Key.End);
        window.RenderForTest();
        controller.State.Staged.ErrorRatePercent.Should().Be(100, "End jumps to the knob's ceiling");

        window.SendFaultKnobKeyForTest(0, Key.Home);
        window.RenderForTest();
        controller.State.Staged.ErrorRatePercent.Should().Be(0, "Home jumps to the knob's floor");

        controller.State.Applied.IsAllZero.Should().BeTrue();
        harness.Faults.Writes.Should().BeEmpty("moving a knob never touches the running system");
    }

    [Fact]
    public void UnarmedTopology_DisablesTheWorkspaceButKeepsEveryValueLegible()
    {
        var harness = new OperatorHarness();
        var controller = harness.CreateController();
        using var window = CreateWindow(controller);

        controller.SelectWorkspace(WorkspaceKind.Faults);
        window.RenderForTest();

        window.IsWorkspaceVisible(WorkspaceKind.Faults).Should().BeTrue("it is disabled, never hidden");
        window.FaultKnobsEnabled.Should().AllBeEquivalentTo(false);
        window.ApplyFaultsButton.Enabled.Should().BeFalse();
        window.FaultsHintText.Should().Contain("Start one with faults armed");
        window.FaultsHintText.Should().Contain("what would be applied");
        window.FaultValueTexts.Should().AllSatisfy(text => text.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public async Task NarrowTerminal_ShortensTheTrackAndKeepsEveryValueTextOnScreen()
    {
        var harness = ArmedHarness();
        var controller = harness.CreateController();
        using var window = CreateWindow(controller);
        await ArmAsync(controller);
        // The widest reading either knob can produce: a staged two-handle band delta.
        controller.StageFaults(new FaultLevels(100, 9500, 12000, 2000));

        window.ResizeForTest(120, 40);
        window.RenderForTest();
        var preferredTrack = window.FaultTrackWidth;
        var preferredColumn = window.FaultValueColumn;

        window.ResizeForTest(80, 24);
        window.RenderForTest();

        window.FaultTrackWidth.Should().BeLessThan(preferredTrack, "the bar degrades first");
        window.FaultValueColumn.Should().BeLessThan(preferredColumn);
        foreach (var text in window.FaultValueTexts)
        {
            text.Should().NotBeNullOrWhiteSpace();
            (window.FaultValueColumn + text.Length).Should().BeLessThanOrEqualTo(
                window.FaultsContentWidth,
                "the printed value is authoritative and must survive the narrow layout intact");
        }
    }

    [Fact]
    public async Task LatencyBand_HasBothHandlesOnTheKeyboard()
    {
        var harness = ArmedHarness();
        var controller = harness.CreateController();
        using var window = CreateWindow(controller);
        await ArmAsync(controller);
        controller.StageFaults(new FaultLevels(0, 800, 2000, 0));
        window.RenderForTest();

        // Plain arrows move the floor; the ceiling stays exactly where it was.
        window.SendFaultKnobKeyForTest(1, Key.CursorRight);
        controller.State.Staged.LatencyFloorMs.Should().Be(1200);
        controller.State.Staged.LatencyCeilingMs.Should().Be(2000);

        window.SendFaultKnobKeyForTest(1, Key.CursorLeft.WithShift);
        controller.State.Staged.LatencyFloorMs.Should().Be(200, "Shift+arrow is the coarse step");

        // Ctrl+arrow moves the ceiling handle, leaving the floor alone.
        window.SendFaultKnobKeyForTest(1, Key.CursorRight.WithCtrl);
        controller.State.Staged.LatencyCeilingMs.Should().Be(3000);
        controller.State.Staged.LatencyFloorMs.Should().Be(200);

        window.SendFaultKnobKeyForTest(1, Key.CursorRight.WithCtrl.WithShift);
        controller.State.Staged.LatencyCeilingMs.Should().Be(9500);

        window.SendFaultKnobKeyForTest(1, Key.End);
        controller.State.Staged.LatencyCeilingMs.Should().Be(12000, "End raises the ceiling to its maximum");
        window.SendFaultKnobKeyForTest(1, Key.Home);
        controller.State.Staged.LatencyFloorMs.Should().Be(0, "Home drops the floor to zero");

        controller.State.Applied.IsAllZero.Should().BeTrue("moving a handle only ever stages");
        harness.Faults.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task LatencyBand_PushingTheFloorPastTheCeilingOrdersTheBandRatherThanInvertingIt()
    {
        var harness = ArmedHarness();
        var controller = harness.CreateController();
        using var window = CreateWindow(controller);
        await ArmAsync(controller);
        controller.StageFaults(new FaultLevels(0, 800, 1200, 0));
        window.RenderForTest();

        window.SendFaultKnobKeyForTest(1, Key.CursorRight.WithShift);

        var staged = controller.State.Staged;
        staged.LatencyFloorMs.Should().BeLessThanOrEqualTo(staged.LatencyCeilingMs);
    }

    [Fact]
    public async Task ArmingToggle_IsReadOnlyOnARunningTopologyAndNamesItsLaunchTimeMeaning()
    {
        var harness = ArmedHarness();
        var controller = harness.CreateController();
        using var window = CreateWindow(controller);
        window.RenderForTest();

        window.ArmingButton.Enabled.Should().BeTrue();
        window.ArmingButton.Text.Should().Be("Faults not armed on next AppHost start", "Dev Proxy is opt-in");
        window.TriggerArmingToggleForTest();
        window.RenderForTest();
        window.ArmingButton.Text.Should().Be("Faults armed on next AppHost start");

        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        window.RenderForTest();

        window.ArmingButton.Enabled.Should().BeFalse();
        window.ArmingButton.Text.Should().Contain("restart it to change");
        harness.Processes.ArmFaultsRequests.Should().Equal(true);
    }

    [Fact]
    public async Task PresetChips_WrapToASecondRowBeforeHidingAnyPreset()
    {
        var harness = ArmedHarness();
        var controller = harness.CreateController();
        using var window = CreateWindow(controller);
        await ArmAsync(controller);

        window.ResizeForTest(56, 24);
        window.RenderForTest();

        window.VisiblePresetNames.Should().Equal("All off", "Regular profile");
        window.PresetLabelText.Should().NotContain("not shown");
    }

    [Fact]
    public async Task PresetChips_ThatCannotFitAtAllAreReported_NeverSilentlyDropped()
    {
        var harness = ArmedHarness();
        var controller = harness.CreateController();
        using var window = CreateWindow(controller);
        await ArmAsync(controller);

        window.ResizeForTest(45, 24);
        window.RenderForTest();

        window.VisiblePresetNames.Should().NotContain("Regular profile");
        window.PresetLabelText.Should().Contain("1 more preset not shown at this width");
    }

    [Fact]
    public async Task TheWorkspaceStatesTheRestartCostBeforeTheOperatorPaysIt()
    {
        var harness = ArmedHarness();
        var controller = harness.CreateController();
        using var window = CreateWindow(controller);
        await ArmAsync(controller);
        window.RenderForTest();

        window.FaultCostText.Should().Contain("restart the Dev Proxy");
        window.FaultCostText.Should().Contain("can fail for a moment");
    }

    private static OperatorHarness ArmedHarness()
    {
        var harness = new OperatorHarness();
        // The fault chip refuses to claim Armed without a live devproxy resource, so an armed
        // fixture's snapshot has to carry one.
        harness.Aspire.DefaultSnapshot = OperatorHarness.ArmedSnapshot(TopologyProfile.Regular);
        return harness;
    }

    private static async Task ArmAsync(OperatorConsoleController controller)
    {
        controller.SetArming(true).Succeeded.Should().BeTrue();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
    }

    private static MainWindow CreateWindow(
        OperatorConsoleController controller,
        IConfirmationService? confirmation = null) =>
        new(controller, () => Task.CompletedTask, confirmation, startPolling: false, marshalUpdates: false);

    /// <summary>Proves no fault control ever routes through the <c>Y</c> confirmation modal.</summary>
    private sealed class ThrowingConfirmationService : IConfirmationService
    {
        public bool Confirm(ConfirmationRequest request) =>
            throw new InvalidOperationException("Fault changes are never gated behind a confirmation modal.");
    }
}
