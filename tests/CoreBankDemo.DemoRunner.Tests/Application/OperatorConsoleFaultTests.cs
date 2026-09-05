using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application;

/// <summary>
/// One test per row of the spec's I/O and edge-case matrix for the Faults workspace.
/// </summary>
public class OperatorConsoleFaultTests
{
    private static readonly PaymentRequest StandardPayment =
        new("NL91ABNA0417164300", "NL20INGB0001234567", 10m, "EUR", PaymentRail.Standard);

    private static readonly FaultLevels RegularShipped = FaultLevels.CheckedInDefaults(TopologyProfile.Regular);

    // --- Stage then apply -------------------------------------------------

    [Fact]
    public async Task StageThenApply_WritesEveryKnobOnceAndReportsAppliedNotYetObserved()
    {
        var (harness, controller) = await ArmedAsync(RegularShipped);

        controller.StageFaults(RegularShipped with { ErrorRatePercent = 40 });
        var stagedState = controller.State;
        var apply = await controller.ApplyFaultsAsync(CancellationToken.None);

        stagedState.HasStagedFaultChange.Should().BeTrue();
        stagedState.Applied.ErrorRatePercent.Should().Be(5, "staging must change nothing in the running system");
        apply.Succeeded.Should().BeTrue();
        harness.Faults.Writes.Should().ContainSingle();
        harness.Faults.Writes[0].Levels.Should().Be(RegularShipped with { ErrorRatePercent = 40 });
        controller.State.Applied.ErrorRatePercent.Should().Be(40);
        controller.State.HasStagedFaultChange.Should().BeFalse();
        controller.State.FaultsObserved.Should().BeFalse();
        controller.State.Evidence.Last().Kind.Should().Be(EvidenceKind.Fault);
        controller.State.Evidence.Last().Summary.Should().Contain("not yet observed");
    }

    [Fact]
    public async Task Apply_WithNothingStaged_IsRefusedSoARepeatedPressCannotRewriteAnIdenticalConfig()
    {
        var (harness, controller) = await ArmedAsync(RegularShipped);

        var apply = await controller.ApplyFaultsAsync(CancellationToken.None);

        apply.Succeeded.Should().BeFalse();
        apply.Message.Should().Contain("Nothing is staged");
        harness.Faults.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_WriteFailure_SurfacesOnEvidenceKeepsStagedValuesAndLeavesTheChipUnchanged()
    {
        var (harness, controller) = await ArmedAsync(RegularShipped);
        harness.Faults.WriteSucceeds = false;

        controller.StageFaults(RegularShipped with { ErrorRatePercent = 40 });
        var apply = await controller.ApplyFaultsAsync(CancellationToken.None);

        apply.Succeeded.Should().BeFalse();
        controller.State.Staged.ErrorRatePercent.Should().Be(40, "the level the operator dialled in is not discarded");
        controller.State.Applied.Should().Be(RegularShipped, "nothing reached the proxy");
        var record = controller.State.Evidence.Last();
        record.Kind.Should().Be(EvidenceKind.Fault);
        record.Succeeded.Should().BeFalse();
        record.Detail.Should().Contain("read-only");
    }

    // --- Observation ------------------------------------------------------

    [Fact]
    public async Task Observation_APaymentSlowerThanTheAppliedFloorFlipsTheConsoleToInForce()
    {
        var (harness, controller) = await ArmedAsync(FaultLevels.AllZero);
        controller.StageFaults(new FaultLevels(0, 800, 2000, 0));
        await controller.ApplyFaultsAsync(CancellationToken.None);
        controller.State.FaultsObserved.Should().BeFalse();

        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "id", "tx", "Pending", "{}", null, TimeSpan.FromMilliseconds(950)));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        controller.State.FaultsObserved.Should().BeTrue();
    }

    [Fact]
    public async Task Observation_TrafficThatDoesNotCarryTheLevelsLeavesTheConsoleWaiting()
    {
        var (harness, controller) = await ArmedAsync(FaultLevels.AllZero);
        controller.StageFaults(new FaultLevels(0, 800, 2000, 0));
        await controller.ApplyFaultsAsync(CancellationToken.None);

        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "id", "tx", "Pending", "{}", null, TimeSpan.FromMilliseconds(30)));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        controller.State.FaultsObserved.Should().BeFalse();
        controller.State.FaultsAppliedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Observation_AnInjectedErrorStatusAlsoCountsAsProof()
    {
        var (harness, controller) = await ArmedAsync(FaultLevels.AllZero);
        controller.StageFaults(new FaultLevels(40, 0, 0, 0));
        await controller.ApplyFaultsAsync(CancellationToken.None);

        harness.Payments.QueueInspections(new InspectionResult(false, 503, "outcome", null, "injected", TimeSpan.FromMilliseconds(4)));
        await controller.QueryOutcomeAsync("tx-1", CancellationToken.None);

        controller.State.FaultsObserved.Should().BeTrue();
    }

    // --- Apply all-zero ---------------------------------------------------

    [Fact]
    public async Task ApplyAllZero_LandsOnArmedImmediatelyWithNoObservationWait()
    {
        var (harness, controller) = await ArmedAsync(RegularShipped);

        controller.StageFaults(FaultLevels.AllZero);
        var apply = await controller.ApplyFaultsAsync(CancellationToken.None);

        apply.Succeeded.Should().BeTrue();
        harness.Faults.Writes.Should().ContainSingle().Which.Levels.Should().Be(FaultLevels.AllZero);
        controller.State.Applied.IsAllZero.Should().BeTrue();
        controller.State.FaultsObserved.Should().BeTrue();
        controller.State.Evidence.Last().FaultLevels.Should().BeNull("zero levels are not a fault in force");
    }

    // --- Panic-off mid-burst ----------------------------------------------

    [Fact]
    public async Task PanicOff_DuringABurst_AppliesInOneStepAndNeverCancelsOrDelaysTheBurst()
    {
        var (harness, controller) = await ArmedAsync(RegularShipped);
        harness.Payments.ReleaseSubmission = new TaskCompletionSource();
        harness.Payments.SubmissionStarted = new TaskCompletionSource();
        var burst = controller.RunBurstAsync(StandardPayment, 4, 1, CancellationToken.None);
        await harness.Payments.SubmissionStarted.Task;

        controller.State.ActiveMutation!.Kind.Should().Be(MutationKind.PaymentBurst);
        var panic = await controller.PanicOffAsync(CancellationToken.None);

        panic.Succeeded.Should().BeTrue("fault controls are exempt from the single-action-in-flight lock");
        controller.State.Applied.IsAllZero.Should().BeTrue();
        controller.State.ActiveMutation!.Kind.Should().Be(MutationKind.PaymentBurst, "the burst still holds the lock");

        harness.Payments.ReleaseSubmission.SetResult();
        await burst;
        controller.State.Burst.Cancelled.Should().BeFalse();
        controller.State.Burst.Sent.Should().Be(4);
    }

    [Fact]
    public async Task PanicOff_WriteFailure_LeavesEveryKnobAtItsLastAppliedValue()
    {
        var (harness, controller) = await ArmedAsync(RegularShipped);
        controller.StageFaults(RegularShipped with { ErrorRatePercent = 60 });
        harness.Faults.WriteSucceeds = false;

        var panic = await controller.PanicOffAsync(CancellationToken.None);

        panic.Succeeded.Should().BeFalse();
        controller.State.Applied.Should().Be(RegularShipped);
        controller.State.Staged.Should().Be(RegularShipped);
        controller.State.Evidence.Last().Summary.Should().Contain("Panic-off failed");
    }

    [Fact]
    public async Task ApplyAndPanicOff_NeverTakeTheSingleActionInFlightLock()
    {
        var (_, controller) = await ArmedAsync(RegularShipped);

        controller.StageFaults(RegularShipped with { ErrorRatePercent = 25 });
        await controller.ApplyFaultsAsync(CancellationToken.None);
        await controller.PanicOffAsync(CancellationToken.None);

        controller.State.ActiveMutation.Should().BeNull();
        controller.State.Evidence.Should().NotContain(record => record.Summary.Contains("ApplyFaults"));
    }

    // --- Unarmed topology -------------------------------------------------

    [Fact]
    public async Task UnarmedTopology_RefusesEveryFaultCommandWithAReasonAndAOneStepRemedy()
    {
        var harness = new OperatorHarness();
        var controller = harness.CreateController();

        controller.State.FaultArmingRequested.Should().BeFalse("Dev Proxy is opt-in");
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        controller.State.FaultsArmed.Should().BeFalse();
        harness.Processes.ArmFaultsRequests.Should().Equal(false);
        controller.StageFaults(RegularShipped).Succeeded.Should().BeFalse();
        (await controller.ApplyFaultsAsync(CancellationToken.None)).Succeeded.Should().BeFalse();
        var panic = await controller.PanicOffAsync(CancellationToken.None);
        panic.Message.Should().Contain("start it again with faults armed");
        harness.Faults.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task AttachedTopology_IsNeverArmedAndItsArmingCannotBeChanged()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Discovered = [OperatorHarness.Snapshot(TopologyProfile.Regular)];
        harness.Faults.ReadOverride = RegularShipped;
        var controller = harness.CreateController();
        await controller.InitializeAsync(CancellationToken.None);
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);

        controller.State.Ownership.Should().Be(TopologyOwnership.Attached);
        controller.State.FaultsArmed.Should().BeFalse();
        controller.State.Applied.Should().Be(RegularShipped, "the sliders still read what is really in force");
        var arming = controller.SetArming(false);
        arming.Succeeded.Should().BeFalse();
        arming.Message.Should().Contain("not owned by this session");
        OperatorConsoleController.FaultsUnavailableReason(controller.State).Should().Contain("Attached");
    }

    [Fact]
    public async Task SetArming_OnARunningOwnedTopology_IsReadOnlyWithTheRestartRemedy()
    {
        var harness = new OperatorHarness();
        var controller = harness.CreateController();
        controller.SetArming(true).Succeeded.Should().BeTrue();

        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        var arming = controller.SetArming(false);

        arming.Succeeded.Should().BeFalse();
        arming.Message.Should().Contain("start it again");
        controller.State.FaultArmingRequested.Should().BeTrue();
    }

    // --- Cold start / stale session config --------------------------------

    [Fact]
    public async Task ColdStart_ReadsTheLevelsActuallyInForceRatherThanAnInventedZero()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Discovered = [OperatorHarness.Snapshot(TopologyProfile.LoadTests)];
        harness.Faults.ReadOverride = new FaultLevels(0, 9500, 12000, 0);
        var controller = harness.CreateController();
        await controller.InitializeAsync(CancellationToken.None);

        await controller.AttachAsync(TopologyProfile.LoadTests, CancellationToken.None);

        harness.Faults.Reads.Should().Contain(TopologyProfile.LoadTests);
        controller.State.Applied.Should().Be(new FaultLevels(0, 9500, 12000, 0));
        controller.State.Staged.Should().Be(controller.State.Applied);
        controller.State.FaultsObserved.Should().BeFalse("reading a config file sets sliders, never proof");
    }

    [Fact]
    public async Task ColdStart_UnreadableConfig_FallsBackToDefaultsAndNamesTheFailedRead()
    {
        var harness = new OperatorHarness();
        harness.Faults.ReadSucceeds = false;
        harness.Faults.ReadError = "devproxyrc.json could not be parsed";
        harness.Faults.ReadOverride = RegularShipped;
        var controller = harness.CreateController();
        controller.SetArming(true);

        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        controller.State.Applied.Should().Be(RegularShipped);
        controller.State.FaultDetail.Should().Contain("could not be parsed");
        controller.State.Evidence.Should().Contain(record =>
            record.Kind == EvidenceKind.Fault && !record.Succeeded && record.Method == "READ");
    }

    [Fact]
    public async Task Arming_RewritesTheSessionConfigSoAPriorSessionsLevelsNeverSilentlyApply()
    {
        var harness = new OperatorHarness();
        var controller = harness.CreateController();
        controller.SetArming(true);

        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        harness.Faults.Resets.Should().Equal(TopologyProfile.Regular);
        harness.Processes.ArmFaultsRequests.Should().Equal(true);
        controller.State.FaultsArmed.Should().BeTrue();
        controller.State.Applied.IsAllZero.Should().BeTrue();
    }

    // --- Provenance -------------------------------------------------------

    [Fact]
    public async Task EvidenceCapturedUnderFaults_IsStampedWithTheLevelsInForce()
    {
        var (harness, controller) = await ArmedAsync(FaultLevels.AllZero);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "a", "a", "Pending", "{}", null, TimeSpan.FromMilliseconds(5)));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        controller.StageFaults(new FaultLevels(40, 0, 0, 0));
        await controller.ApplyFaultsAsync(CancellationToken.None);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "b", "b", "Pending", "{}", null, TimeSpan.FromMilliseconds(5)));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        var payments = controller.State.Evidence.Where(record => record.Kind == EvidenceKind.Payment).ToList();
        payments[0].FaultLevels.Should().BeNull();
        payments[1].FaultLevels.Should().Be(new FaultLevels(40, 0, 0, 0));
    }

    [Fact]
    public async Task StoppingTheTopology_ClearsTheArmedStateAndItsLevels()
    {
        var (_, controller) = await ArmedAsync(RegularShipped);

        await controller.StopAsync(CancellationToken.None);

        controller.State.FaultsArmed.Should().BeFalse();
        controller.State.Applied.IsAllZero.Should().BeTrue();
        controller.State.FaultsAppliedAt.Should().BeNull();
    }

    private static async Task<(OperatorHarness Harness, OperatorConsoleController Controller)> ArmedAsync(
        FaultLevels levels)
    {
        var harness = new OperatorHarness();
        harness.Aspire.DefaultSnapshot = OperatorHarness.ArmedSnapshot(TopologyProfile.Regular);
        var controller = harness.CreateController();
        controller.SetArming(true).Succeeded.Should().BeTrue();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        if (!levels.IsAllZero)
        {
            // An armed start always resets the session config quiet, so a non-zero starting
            // point is reached the only way the console offers: stage, then Apply.
            controller.StageFaults(levels);
            await controller.ApplyFaultsAsync(CancellationToken.None);
        }

        harness.Faults.Writes.Clear();
        return (harness, controller);
    }

    // --- The generated config must not outlive the session ------------------

    [Fact]
    public async Task Stop_RemovesTheGeneratedSessionConfigSoItCannotShadowTheCheckedInProfile()
    {
        var (harness, controller) = await ArmedAsync(RegularShipped);

        await controller.StopAsync(CancellationToken.None);

        harness.Faults.Deletes.Should().Equal(TopologyProfile.Regular);
    }

    [Fact]
    public async Task Stop_DeleteFailure_IsReportedRatherThanSwallowed()
    {
        var (harness, controller) = await ArmedAsync(RegularShipped);
        harness.Faults.DeleteSucceeds = false;

        await controller.StopAsync(CancellationToken.None);

        controller.State.Evidence.Should().Contain(record =>
            record.Kind == EvidenceKind.Fault
            && !record.Succeeded
            && record.Method == "DELETE"
            && record.Summary.Contains("shadow the checked-in profile"));
    }

    [Fact]
    public async Task Stop_OnATopologyThisSessionDidNotArm_DeletesNothing()
    {
        var harness = new OperatorHarness();
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        await controller.StopAsync(CancellationToken.None);

        harness.Faults.Deletes.Should().BeEmpty("a config another session left behind is not ours to remove");
    }

    [Fact]
    public async Task Quit_RemovesTheGeneratedSessionConfigForTheOwnedTopology()
    {
        var (harness, controller) = await ArmedAsync(FaultLevels.AllZero);

        await controller.ShutdownAsync(CancellationToken.None);

        harness.Faults.Deletes.Should().Equal(TopologyProfile.Regular);
    }

    [Fact]
    public async Task Switch_ArmsTheTargetAndRemovesTheConfigOfTheTopologyBeingLeft()
    {
        var harness = new OperatorHarness();
        harness.Aspire.DefaultSnapshot = OperatorHarness.ArmedSnapshot(TopologyProfile.Regular);
        var controller = harness.CreateController();
        controller.SetArming(true);
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Faults.Resets.Clear();
        harness.Aspire.DefaultSnapshot = OperatorHarness.ArmedSnapshot(TopologyProfile.LoadTests);
        harness.Preflight.Report = FakePreflightRunner.ReadyReport();

        var result = await controller.SwitchAsync(TopologyProfile.LoadTests, CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Message);
        harness.Faults.Deletes.Should().Equal([TopologyProfile.Regular]);
        harness.Faults.Resets.Should().Equal([TopologyProfile.LoadTests], "the incoming topology is armed quiet");
        harness.Processes.ArmFaultsRequests.Should().Equal(true, true);
        harness.Faults.Reads.Should().Contain(TopologyProfile.LoadTests);
        controller.State.Profile.Should().Be(TopologyProfile.LoadTests);
        controller.State.FaultsArmed.Should().BeTrue();
        controller.State.Applied.IsAllZero.Should().BeTrue();
    }

    // --- Observation must be bounded, not merely exceeded -------------------

    [Fact]
    public async Task Observation_AZeroLatencyFloorIsNeverProof()
    {
        var (harness, controller) = await ArmedAsync(FaultLevels.AllZero);
        // Throttling only: no floor, so no duration can ever prove this level is live.
        controller.StageFaults(new FaultLevels(0, 0, 0, 100));
        await controller.ApplyFaultsAsync(CancellationToken.None);

        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "id", "tx", "Pending", "{}", null, TimeSpan.FromSeconds(30)));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        controller.State.FaultsObserved.Should().BeFalse();
    }

    [Fact]
    public async Task Observation_ADurationWildlyBeyondTheCeilingIsARealOutage_NotProof()
    {
        var (harness, controller) = await ArmedAsync(FaultLevels.AllZero);
        controller.StageFaults(new FaultLevels(0, 800, 2000, 0));
        await controller.ApplyFaultsAsync(CancellationToken.None);

        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "id", "tx", "Pending", "{}", null, TimeSpan.FromSeconds(60)));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        controller.State.FaultsObserved.Should().BeFalse();
    }

    [Fact]
    public async Task Observation_AnAggregateBurstRecordIsNeverProofEvenWhenItOutlastsTheFloor()
    {
        var (harness, controller) = await ArmedAsync(FaultLevels.AllZero);
        controller.StageFaults(new FaultLevels(0, 800, 12000, 0));
        await controller.ApplyFaultsAsync(CancellationToken.None);
        harness.Payments.ReleaseSubmission = new TaskCompletionSource();
        harness.Payments.SubmissionStarted = new TaskCompletionSource();

        var burst = controller.RunBurstAsync(StandardPayment, 2, 1, CancellationToken.None);
        await harness.Payments.SubmissionStarted.Task;
        // Squarely inside the applied 800-12000ms band, so only the record's aggregate nature
        // keeps it from counting: a burst's duration is the batch's, not an intercepted call's.
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.Payments.ReleaseSubmission.SetResult();
        await burst;

        var record = controller.State.Evidence.Last(item => item.Kind == EvidenceKind.Burst);
        controller.State.Applied.IsCarriedByDuration(record.Duration).Should().BeTrue(
            "the guard under test must be the record kind, not the duration");
        controller.State.FaultsObserved.Should().BeFalse();
    }

    // --- Arming is preflighted ---------------------------------------------

    [Fact]
    public async Task Start_WithArmingRequestedButNoDevProxyBinary_IsBlockedBeforeTheAppHostStarts()
    {
        var harness = new OperatorHarness();
        harness.Preflight.DevProxyAvailable = false;
        var controller = harness.CreateController();
        controller.SetArming(true);

        var start = await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        start.Succeeded.Should().BeFalse();
        harness.Processes.StartCount.Should().Be(0);
        harness.Preflight.ArmingRequests.Should().Contain(true);
    }

    [Fact]
    public async Task Start_WithArmingOff_DoesNotRequireTheDevProxyBinary()
    {
        var harness = new OperatorHarness();
        harness.Preflight.DevProxyAvailable = false;
        var controller = harness.CreateController();

        var start = await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        start.Succeeded.Should().BeTrue(start.Message);
        harness.Preflight.ArmingRequests.Should().AllBeEquivalentTo(false);
    }

    // --- Concurrency ---------------------------------------------------------

    [Fact]
    public async Task ApplyAndPanicOff_AreSerialized_SoTheReportedLevelsCannotDisagreeWithTheFile()
    {
        var (harness, controller) = await ArmedAsync(FaultLevels.AllZero);
        controller.StageFaults(new FaultLevels(40, 800, 2000, 100));
        harness.Faults.WriteStarted = new TaskCompletionSource();
        harness.Faults.ReleaseWrite = new TaskCompletionSource();

        // Both are lock-exempt and 0 is bound window-wide, so they genuinely can be fired
        // together; nothing but the commit gate keeps them from interleaving.
        var apply = controller.ApplyFaultsAsync(CancellationToken.None);
        await harness.Faults.WriteStarted.Task;
        var panic = controller.PanicOffAsync(CancellationToken.None);
        harness.Faults.ReleaseWrite.SetResult();
        await Task.WhenAll(apply, panic);

        harness.Faults.MaxConcurrentWrites.Should().Be(1);
        harness.Faults.Writes.Should().HaveCount(2);
        controller.State.Applied.Should().Be(
            harness.Faults.Writes[^1].Levels,
            "the reported level is always the one the last write actually put in the file");
    }

    [Fact]
    public async Task Apply_ThatFinishesAfterTheTopologyChanged_IsDiscardedRatherThanStampedOnTheNewOne()
    {
        var (harness, controller) = await ArmedAsync(FaultLevels.AllZero);
        controller.StageFaults(new FaultLevels(40, 800, 2000, 0));
        harness.Faults.WriteStarted = new TaskCompletionSource();
        harness.Faults.ReleaseWrite = new TaskCompletionSource();

        var apply = controller.ApplyFaultsAsync(CancellationToken.None);
        await harness.Faults.WriteStarted.Task;
        await controller.StopAsync(CancellationToken.None);
        harness.Faults.ReleaseWrite.SetResult();
        var result = await apply;

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("topology changed");
        controller.State.FaultsArmed.Should().BeFalse();
        controller.State.Applied.IsAllZero.Should().BeTrue("nothing was stamped onto the stopped topology");
        controller.State.FaultsAppliedAt.Should().BeNull();
        controller.State.Evidence.Should().Contain(record =>
            record.Kind == EvidenceKind.Fault && record.Summary.Contains("discarded"));
    }
}
