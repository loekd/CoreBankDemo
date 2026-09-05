using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application;

public class OperatorConsoleControllerTests
{
    private static readonly PaymentRequest StandardPayment =
        new("NL91ABNA0417164300", "NL20INGB0001234567", 10m, "EUR", PaymentRail.Standard);

    private static readonly PaymentRequest InstantPayment =
        StandardPayment with { Rail = PaymentRail.Instant };

    [Fact]
    public async Task Initialize_NoTopology_StartsWithEmptyHistoryAndNoImplicitProcess()
    {
        var harness = new OperatorHarness();
        var controller = harness.CreateController();

        await controller.InitializeAsync(CancellationToken.None);

        controller.State.Profile.Should().Be(TopologyProfile.None);
        controller.State.Evidence.Should().BeEmpty();
        controller.State.StatusLine.Should().Contain("No topology active");
        harness.Processes.StartCount.Should().Be(0);
        controller.State.Preflight.Should().NotBeNull();
    }

    [Fact]
    public async Task Initialize_DiscoveryUnreachable_IsStoredAndBlocksStart()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Discovery = TopologyDiscoveryResult.Unreachable("aspire missing");
        var controller = harness.CreateController();

        await controller.InitializeAsync(CancellationToken.None);
        var start = await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        controller.State.Preflight!.DiscoveryReachable.Should().BeFalse();
        controller.State.StatusLine.Should().Contain("Unreachable");
        start.Succeeded.Should().BeFalse();
        harness.Processes.StartCount.Should().Be(0);
    }

    [Fact]
    public async Task Initialize_OneKnownTopology_RequiresExplicitAttach()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Discovered = [OperatorHarness.Snapshot(TopologyProfile.Regular)];
        var controller = harness.CreateController();

        await controller.InitializeAsync(CancellationToken.None);

        controller.State.Profile.Should().Be(TopologyProfile.Regular);
        controller.State.Ownership.Should().Be(TopologyOwnership.None);
        controller.State.StatusLine.Should().Contain("Attach explicitly");
    }

    [Fact]
    public async Task Initialize_ConflictingTopologies_FailsClosed()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Discovered =
        [
            OperatorHarness.Snapshot(TopologyProfile.Regular),
            OperatorHarness.Snapshot(TopologyProfile.LoadTests),
        ];
        var controller = harness.CreateController();

        await controller.InitializeAsync(CancellationToken.None);

        controller.State.Profile.Should().Be(TopologyProfile.None);
        controller.State.StatusLine.Should().Contain("Conflicting");
    }

    [Fact]
    public async Task Initialize_PartialKnownTopology_DisablesDuplicateStart()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Discovered =
        [
            OperatorHarness.Snapshot(TopologyProfile.Regular) with
            {
                IsFingerprintMatch = false,
                ErrorSummary = "partial graph",
            },
        ];
        var controller = harness.CreateController();

        await controller.InitializeAsync(CancellationToken.None);
        var start = await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        controller.State.Profile.Should().Be(TopologyProfile.Regular);
        controller.State.StatusLine.Should().Contain("not attachable");
        start.Succeeded.Should().BeFalse();
        harness.Processes.StartCount.Should().Be(0);
    }

    [Fact]
    public async Task Start_KnownProfile_OwnsVerifiedTopologyAndRecordsEvidence()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();

        var result = await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        controller.State.Ownership.Should().Be(TopologyOwnership.Owned);
        controller.State.RunGeneration.Should().Be(1);
        var topologyRecord = controller.State.Evidence.Single(record => record.Kind == EvidenceKind.Topology);
        topologyRecord.Summary.Should().Contain("Started");
        topologyRecord.Method.Should().StartWith("aspire start --apphost");
        harness.Processes.StartCount.Should().Be(1);
    }

    [Fact]
    public async Task Start_WhenTopologyWasDetected_DoesNotStartDuplicate()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Discovered = [OperatorHarness.Snapshot(TopologyProfile.Regular)];
        var controller = harness.CreateController();
        await controller.InitializeAsync(CancellationToken.None);

        var result = await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        harness.Processes.StartCount.Should().Be(0);
    }

    [Fact]
    public async Task Start_Timeout_RemainsFailureAndIncludesBoundedProcessDetail()
    {
        var harness = new OperatorHarness();
        harness.Aspire.DefaultSnapshot = TopologySnapshot.Unreachable(TopologyProfile.Regular, harness.Time.GetUtcNow(), "missing");
        harness.Processes.Output = new string('x', JournalRedaction.MaxLength + 100);
        var controller = harness.CreateController();

        var result = await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        controller.State.Evidence.Single().Succeeded.Should().BeFalse();
        controller.State.Evidence.Single().Detail.Length.Should().BeLessThanOrEqualTo(JournalRedaction.MaxLength + 1);
        harness.Processes.StopCount.Should().Be(1);
    }

    [Fact]
    public async Task Start_ProcessOwnershipFailure_IsSurfacedWithoutClaimingOwnedState()
    {
        var harness = new OperatorHarness();
        harness.Processes.StartException = new InvalidOperationException("missing verified PID");
        var controller = harness.CreateController();

        var result = await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("verified PID");
        controller.State.Ownership.Should().Be(TopologyOwnership.None);
        controller.OwnedProcessId.Should().BeNull();
    }

    [Fact]
    public async Task Attach_RequiresMatchingFingerprintAndNeverClaimsOwnership()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(
            OperatorHarness.Snapshot(TopologyProfile.Regular, fingerprint: false),
            OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();

        (await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None)).Succeeded.Should().BeFalse();
        (await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None)).Succeeded.Should().BeTrue();

        controller.State.Ownership.Should().Be(TopologyOwnership.Attached);
        harness.Processes.StartCount.Should().Be(0);
    }

    [Fact]
    public async Task AttachedTopology_ForbidsWholeAppHostStopAndSwitch()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);

        var stop = await controller.StopAsync(CancellationToken.None);
        var change = await controller.SwitchAsync(TopologyProfile.LoadTests, CancellationToken.None);

        stop.Succeeded.Should().BeFalse();
        change.Succeeded.Should().BeFalse();
        harness.Processes.StopCount.Should().Be(0);
        harness.Processes.StartCount.Should().Be(0);
    }

    [Fact]
    public async Task OwnedTopology_StopOnlyStopsTrackedChildAndRetainsProvenance()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        var result = await controller.StopAsync(CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        harness.Processes.StopCount.Should().Be(1);
        controller.State.Profile.Should().Be(TopologyProfile.None);
        controller.State.Evidence.Last().Profile.Should().Be(TopologyProfile.Regular);
        controller.State.Evidence.Last().RunGeneration.Should().Be(1);
    }

    [Fact]
    public async Task OwnedTopology_Stop_ReturnsRejectedWhenStopOwnedAsyncThrows()
    {
        // Patch 7a regression test: StopOwnedAsync can throw (e.g. an
        // ownership/PID verification failure, an "aspire ps" timeout) --
        // StopAsync must surface that as a Rejected result the UI can
        // display, releasing the mutation lock, rather than propagating.
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Processes.StopException = new InvalidOperationException("ownership verification failed");

        var result = await controller.StopAsync(CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("ownership verification failed");
        controller.State.ActiveMutation.Should().BeNull("the mutation lock must be released on failure");
        controller.State.Ownership.Should().Be(TopologyOwnership.Owned, "a failed stop must not silently clear ownership");
    }

    [Fact]
    public async Task Switch_OwnedTopology_StopsStartsAndIncrementsGeneration()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(
            OperatorHarness.Snapshot(TopologyProfile.Regular),
            OperatorHarness.Snapshot(TopologyProfile.LoadTests));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        var result = await controller.SwitchAsync(TopologyProfile.LoadTests, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        harness.Processes.StopCount.Should().Be(1);
        harness.Processes.StartCount.Should().Be(2);
        controller.State.Profile.Should().Be(TopologyProfile.LoadTests);
        controller.State.RunGeneration.Should().Be(2);
    }

    [Fact]
    public async Task Switch_RechecksTargetBeforeStoppingOwnedSource()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Preflight.Report = FakePreflightRunner.ReadyReport(
            loadTests: OperatorHarness.Snapshot(TopologyProfile.LoadTests));

        var result = await controller.SwitchAsync(TopologyProfile.LoadTests, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("before stopping");
        harness.Processes.StopCount.Should().Be(0);
        controller.State.Profile.Should().Be(TopologyProfile.Regular);
    }

    [Fact]
    public async Task Switch_TargetHealthTimeout_CleansTargetOwnedProcess()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Aspire.DefaultSnapshot = TopologySnapshot.Unreachable(
            TopologyProfile.LoadTests,
            harness.Time.GetUtcNow(),
            "not ready");

        var result = await controller.SwitchAsync(TopologyProfile.LoadTests, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        harness.Processes.StopCount.Should().Be(2);
        controller.OwnedProcessId.Should().BeNull();
    }

    [Fact]
    public async Task ResourceCommand_AttachedFreshFingerprint_IsAllowedAndConfirmedFromSnapshot()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Aspire.Queue(OperatorHarness.Snapshot(
            TopologyProfile.Regular,
            resources:
            [
                new ResourceSnapshot(KnownResources.CoreBankApi, ResourceCondition.Stopped, "Stopped", []),
                .. OperatorHarness.DefaultResources(TopologyProfile.Regular).Where(resource => resource.Name != KnownResources.CoreBankApi),
            ]));

        var result = await controller.ExecuteResourceCommandAsync(
            KnownResources.CoreBankApi,
            ResourceCommand.Stop,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        harness.Aspire.Commands.Should().ContainSingle();
        controller.State.Topology!.FindResource(KnownResources.CoreBankApi)!.Condition.Should().Be(ResourceCondition.Stopped);
    }

    [Fact]
    public async Task ResourceCommand_StaleFingerprintOrUnknownResource_IsRejected()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromSeconds(6));

        var stale = await controller.ExecuteResourceCommandAsync(
            KnownResources.CoreBankApi,
            ResourceCommand.Restart,
            CancellationToken.None);
        var unknown = await controller.ExecuteResourceCommandAsync(
            "arbitrary-shell-target",
            ResourceCommand.Restart,
            CancellationToken.None);

        stale.Succeeded.Should().BeFalse();
        unknown.Succeeded.Should().BeFalse();
        harness.Aspire.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task ResourceCommand_DispatchFailure_DoesNotClaimSuccess()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Aspire.CommandResult = ResourceCommandResult.Rejected("command rejected");

        var result = await controller.ExecuteResourceCommandAsync(
            KnownResources.CoreBankApi,
            ResourceCommand.Restart,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        controller.State.Evidence.Last().Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ResourceCommand_PartialReplicaFailure_ReconcilesAndRequiresManualRefresh()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Aspire.CommandResult = new ResourceCommandResult(
            ResourceDispatchStatus.Partial,
            "second replica failed",
            ["corebank-api-1"],
            ["corebank-api-2"]);
        harness.Aspire.Queue(OperatorHarness.Snapshot(
            TopologyProfile.Regular,
            resources:
            [
                new ResourceSnapshot(
                    KnownResources.CoreBankApi,
                    ResourceCondition.Degraded,
                    "Degraded",
                    ["http://127.0.0.1:5032"],
                    2,
                    "one running, one failed",
                    ["corebank-api-1", "corebank-api-2"]),
                .. OperatorHarness.DefaultResources(TopologyProfile.Regular).Where(item => item.Name != KnownResources.CoreBankApi),
            ]));

        var result = await controller.ExecuteResourceCommandAsync(
            KnownResources.CoreBankApi,
            ResourceCommand.Restart,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Partial mutation");
        controller.State.Topology!.FindResource(KnownResources.CoreBankApi)!.Condition.Should().Be(ResourceCondition.Degraded);
        controller.State.ResourceAuthorityAvailable.Should().BeFalse();
        controller.State.Evidence.Last().Detail.Should().Contain("second replica failed");
    }

    [Fact]
    public async Task ResourceCommand_AmbiguousDispatch_RequiresRefreshBeforeAnotherMutation()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Aspire.CommandResult = new ResourceCommandResult(
            ResourceDispatchStatus.Ambiguous,
            "timed out after dispatch",
            [],
            ["corebank-api-1"]);
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));

        var first = await controller.ExecuteResourceCommandAsync(
            KnownResources.CoreBankApi,
            ResourceCommand.Restart,
            CancellationToken.None);
        var second = await controller.ExecuteResourceCommandAsync(
            KnownResources.CoreBankApi,
            ResourceCommand.Restart,
            CancellationToken.None);

        first.Message.Should().Contain("Ambiguous");
        second.Message.Should().Contain("fresh");
        harness.Aspire.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task ResourceCommand_ConfirmationTimeoutAfterSuccessfulDispatch_IsAmbiguous()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Aspire.CommandResult = new ResourceCommandResult(
            ResourceDispatchStatus.Dispatched,
            "both dispatched",
            ["corebank-api-1", "corebank-api-2"],
            []);
        harness.Aspire.DefaultSnapshot = OperatorHarness.Snapshot(TopologyProfile.Regular);

        var result = await controller.ExecuteResourceCommandAsync(
            KnownResources.CoreBankApi,
            ResourceCommand.Stop,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Ambiguous after dispatch");
        controller.State.ResourceAuthorityAvailable.Should().BeFalse();
        controller.State.Evidence.Last().Method.Should().Contain("corebank-api-1 stop")
            .And.Contain("corebank-api-2 stop");
    }

    [Fact]
    public async Task Refresh_ExternalStateChange_IsDebouncedUntilSecondSnapshot()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        var changed = OperatorHarness.Snapshot(
            TopologyProfile.Regular,
            resources:
            [
                new ResourceSnapshot(KnownResources.CoreBankApi, ResourceCondition.Stopped, "Stopped", []),
                .. OperatorHarness.DefaultResources(TopologyProfile.Regular).Where(resource => resource.Name != KnownResources.CoreBankApi),
            ]);
        harness.Aspire.Queue(changed, changed);

        await controller.RefreshAsync(CancellationToken.None);
        controller.State.Topology!.FindResource(KnownResources.CoreBankApi)!.Condition.Should().Be(ResourceCondition.Healthy);
        (await controller.ExecuteResourceCommandAsync(
            KnownResources.CoreBankApi,
            ResourceCommand.Restart,
            CancellationToken.None)).Succeeded.Should().BeFalse();

        await controller.RefreshAsync(CancellationToken.None);
        controller.State.Topology!.FindResource(KnownResources.CoreBankApi)!.Condition.Should().Be(ResourceCondition.Stopped);
    }

    [Fact]
    public async Task Refresh_UnreachableSnapshot_IsDistinctAndDisablesAuthority()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        var unreachable = TopologySnapshot.Unreachable(TopologyProfile.Regular, harness.Time.GetUtcNow(), "CLI transport failed");
        harness.Aspire.Discovered = [unreachable];
        harness.Aspire.Queue(unreachable);

        await controller.RefreshAsync(CancellationToken.None);
        var command = await controller.ExecuteResourceCommandAsync(
            KnownResources.CoreBankApi,
            ResourceCommand.Restart,
            CancellationToken.None);

        controller.State.StatusLine.Should().Contain("Unreachable");
        command.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_DiscoveryTransportFailure_PreservesValidAttachmentAndBlocksDuplicateStart()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Aspire.Queue(TopologySnapshot.Unreachable(TopologyProfile.Regular, harness.Time.GetUtcNow(), "describe timeout"));
        harness.Aspire.Discovery = TopologyDiscoveryResult.Unreachable("ps timeout");

        await controller.RefreshAsync(CancellationToken.None);
        var start = await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        controller.State.Profile.Should().Be(TopologyProfile.Regular);
        controller.State.Ownership.Should().Be(TopologyOwnership.Attached);
        controller.State.Topology!.IsReachable.Should().BeFalse();
        start.Succeeded.Should().BeFalse();
        harness.Processes.StartCount.Should().Be(0);
    }

    [Fact]
    public async Task Refresh_AttachedTopologyConfirmedGone_ClearsAttachment()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Aspire.Queue(TopologySnapshot.Unreachable(TopologyProfile.Regular, harness.Time.GetUtcNow(), "not running"));
        harness.Aspire.Discovered = [];

        await controller.RefreshAsync(CancellationToken.None);

        controller.State.Profile.Should().Be(TopologyProfile.None);
        controller.State.Ownership.Should().Be(TopologyOwnership.None);
        controller.State.StatusLine.Should().Contain("no longer running");
    }

    [Fact]
    public async Task Refresh_OwnedTopologyConfirmedGone_ClearsOwnershipTruthfully()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Aspire.Queue(TopologySnapshot.Unreachable(TopologyProfile.Regular, harness.Time.GetUtcNow(), "gone"));
        harness.Aspire.Discovery = TopologyDiscoveryResult.Success([]);

        await controller.RefreshAsync(CancellationToken.None);

        controller.State.Profile.Should().Be(TopologyProfile.None);
        controller.OwnedProcessId.Should().BeNull();
        harness.Processes.ForgetCount.Should().Be(1);
        controller.State.Evidence.Last().Summary.Should().Contain("Owned AppHost disappeared");
        controller.State.Evidence.Last().RunGeneration.Should().Be(1);
    }

    [Fact]
    public async Task Refresh_OwnedTopologyConfirmedGone_SelfHealsOwnershipEvenWhenForgetExitedOwnedAsyncThrows()
    {
        // Patch 7b regression test: ForgetExitedOwnedAsync can throw (e.g. an
        // ownership-verification failure or an "aspire ps" timeout) -- the
        // refresh must not abort before clearing stale ownership, or the
        // console would keep showing an Owned AppHost that no longer exists.
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Aspire.Queue(TopologySnapshot.Unreachable(TopologyProfile.Regular, harness.Time.GetUtcNow(), "gone"));
        harness.Aspire.Discovery = TopologyDiscoveryResult.Success([]);
        harness.Processes.ForgetException = new InvalidOperationException("aspire ps timed out while verifying AppHost ownership");

        await controller.RefreshAsync(CancellationToken.None);

        controller.State.Profile.Should().Be(TopologyProfile.None);
        controller.State.Ownership.Should().Be(TopologyOwnership.None, "the console must self-heal rather than get stuck showing a dead Owned AppHost");
        controller.OwnedProcessId.Should().BeNull();
    }

    [Fact]
    public async Task StandardAndInstantPayments_KeepTruthfulWireSemantics()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(
            Payment(PaymentOutcome.Pending, 202, "Pending"),
            Payment(PaymentOutcome.Completed, 200, "Completed"),
            Payment(PaymentOutcome.Failed, 200, "Failed"));

        var standard = await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        var instantCompleted = await controller.SubmitPaymentAsync(InstantPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        var instantFailed = await controller.SubmitPaymentAsync(InstantPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        standard.Outcome.Should().Be(PaymentOutcome.Pending);
        instantCompleted.Outcome.Should().Be(PaymentOutcome.Completed);
        instantFailed.Outcome.Should().Be(PaymentOutcome.Failed);
        controller.State.Evidence.Select(record => record.Summary).Should().Contain(summary => summary.Contains("202 Pending"));
    }

    [Fact]
    public async Task Payment_WrongStatusForRail_IsReportedAsTransportFailure()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(
            Payment(PaymentOutcome.Completed, 200, "Completed"),
            Payment(PaymentOutcome.Completed, 201, "Completed"));

        var standard = await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        var instant = await controller.SubmitPaymentAsync(InstantPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        standard.Outcome.Should().Be(PaymentOutcome.TransportFailure);
        instant.Outcome.Should().Be(PaymentOutcome.TransportFailure);
    }

    [Fact]
    public async Task GeneratedAndSuppliedResend_ReuseExactStableKey()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);

        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        await controller.ResendLastPaymentAsync(CancellationToken.None);
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Supplied, "speaker-key", CancellationToken.None);
        await controller.ResendLastPaymentAsync(CancellationToken.None);

        harness.Payments.Submissions[0].IdempotencyKey.Should().Be("generated-key");
        harness.Payments.Submissions[1].IdempotencyKey.Should().Be("generated-key");
        harness.Payments.Submissions[2].IdempotencyKey.Should().Be("speaker-key");
        harness.Payments.Submissions[3].IdempotencyKey.Should().Be("speaker-key");
    }

    [Fact]
    public async Task OmittedKeyAmbiguity_DisablesResendAndKeepsHonestLabel()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Ambiguous, 0, null, "reply lost"));

        var result = await controller.SubmitPaymentAsync(
            StandardPayment,
            IdempotencyMode.Omitted,
            null,
            CancellationToken.None);
        var resend = await controller.ResendLastPaymentAsync(CancellationToken.None);

        result.Outcome.Should().Be(PaymentOutcome.Ambiguous);
        harness.Payments.Submissions.Single().IdempotencyKey.Should().BeNull();
        controller.State.CanResendLastPayment.Should().BeFalse();
        resend.Outcome.Should().Be(PaymentOutcome.Rejected);
        controller.State.Evidence.Last().Summary.Should().Contain("Ambiguous");
    }

    [Fact]
    public async Task InvalidPayment_IsBlockedBeforeTransport()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        var invalid = StandardPayment with { Amount = 0, Currency = "eur", ToAccount = StandardPayment.FromAccount };

        var result = await controller.SubmitPaymentAsync(invalid, IdempotencyMode.Supplied, "", CancellationToken.None);

        result.Outcome.Should().Be(PaymentOutcome.Rejected);
        result.ErrorSummary.Should().Contain("Amount").And.Contain("Currency").And.Contain("differ").And.Contain("key");
        harness.Payments.Submissions.Should().BeEmpty();
    }

    [Fact]
    public async Task MutationLock_SuppressesDuplicateMutationButAllowsReadOnlyQuery()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.SubmissionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.ReleaseSubmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        await harness.Payments.SubmissionStarted.Task;
        var duplicate = await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        var query = await controller.QueryOutcomeAsync("generated-key", CancellationToken.None);
        harness.Payments.ReleaseSubmission.SetResult();
        await first;

        duplicate.Outcome.Should().Be(PaymentOutcome.Rejected);
        query.Succeeded.Should().BeTrue();
        harness.Payments.Submissions.Should().ContainSingle();
    }

    [Fact]
    public async Task Burst_UsesBoundedConcurrencyDeterministicKeysAndReportsTotals()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);

        var result = await controller.RunBurstAsync(StandardPayment, 5, 2, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        harness.Payments.Submissions.Select(item => item.IdempotencyKey).Should().OnlyHaveUniqueItems();
        harness.Payments.Submissions.Should().OnlyContain(item =>
            item.IdempotencyKey!.Contains("-g001-r001-", StringComparison.Ordinal)
            && item.IdempotencyKey.StartsWith("demo-burst-", StringComparison.Ordinal));
        controller.State.Burst.Should().Be(new BurstProgress(5, 5, 5, 0, 0, false));
    }

    [Fact]
    public async Task Burst_WireContractViolation_IsCountedAsFailure()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Completed, 200, "Completed"));

        var result = await controller.RunBurstAsync(StandardPayment, 1, 1, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        controller.State.Burst.Failed.Should().Be(1);
        controller.State.Evidence.Last().Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(501, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 21)]
    public async Task Burst_OutOfBounds_IsRejected(int count, int concurrency)
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);

        var result = await controller.RunBurstAsync(StandardPayment, count, concurrency, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        harness.Payments.Submissions.Should().BeEmpty();
    }

    [Fact]
    public async Task Burst_CancelIsSoleMutationExceptionAndPreservesPartialEvidence()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.SubmissionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.ReleaseSubmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var burst = controller.RunBurstAsync(StandardPayment, 5, 1, CancellationToken.None);
        await harness.Payments.SubmissionStarted.Task;
        var duplicate = await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        controller.CancelActiveBurst().Should().BeTrue();
        var result = await burst;

        duplicate.Outcome.Should().Be(PaymentOutcome.Rejected);
        result.Succeeded.Should().BeFalse();
        controller.State.Burst.Cancelled.Should().BeTrue();
        controller.State.Evidence.Last().Summary.Should().Contain("cancelled");
        controller.CancelActiveBurst().Should().BeFalse();
    }

    [Fact]
    public async Task OutcomeAndInspection_CreateReadOnlyEvidence()
    {
        var (controller, _) = await AttachedControllerAsync(TopologyProfile.LoadTests);

        await controller.QueryOutcomeAsync("known-id", CancellationToken.None);
        await controller.InspectAsync(KnownEndpoints.PaymentsOutbox, CancellationToken.None);

        controller.State.Evidence.Select(record => record.Kind)
            .Should().Contain([EvidenceKind.OutcomeQuery, EvidenceKind.Inspection]);
    }

    [Fact]
    public async Task Evidence_PreservesOriginalProfileAndGenerationAcrossSwitch()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(
            OperatorHarness.Snapshot(TopologyProfile.Regular),
            OperatorHarness.Snapshot(TopologyProfile.LoadTests));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        await controller.SwitchAsync(TopologyProfile.LoadTests, CancellationToken.None);
        await controller.InspectAsync(KnownEndpoints.CoreBankInbox, CancellationToken.None);

        controller.State.Evidence.Should().Contain(record => record.Profile == TopologyProfile.Regular && record.RunGeneration == 1);
        controller.State.Evidence.Should().Contain(record => record.Profile == TopologyProfile.LoadTests && record.RunGeneration == 2);
    }

    [Fact]
    public async Task AsyncQuery_CapturesProvenanceBeforeConcurrentSwitch()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(
            OperatorHarness.Snapshot(TopologyProfile.Regular),
            OperatorHarness.Snapshot(TopologyProfile.LoadTests));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.QueryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.ReleaseQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var query = controller.QueryOutcomeAsync("old-key", CancellationToken.None);
        await harness.Payments.QueryStarted.Task;
        await controller.SwitchAsync(TopologyProfile.LoadTests, CancellationToken.None);
        harness.Payments.ReleaseQuery.SetResult();
        await query;

        harness.Payments.QueryProfiles.Should().ContainSingle().Which.Should().Be(TopologyProfile.Regular);
        controller.State.Evidence.Last(record => record.Kind == EvidenceKind.OutcomeQuery).Profile.Should().Be(TopologyProfile.Regular);
        controller.State.Evidence.Last(record => record.Kind == EvidenceKind.OutcomeQuery).RunGeneration.Should().Be(1);
    }

    [Fact]
    public async Task AsyncPayment_CapturesOldProvenanceWhenOwnedTopologyDisappears()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.SubmissionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.ReleaseSubmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var payment = controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        await harness.Payments.SubmissionStarted.Task;
        harness.Aspire.Queue(TopologySnapshot.Unreachable(TopologyProfile.Regular, harness.Time.GetUtcNow(), "gone"));
        harness.Aspire.Discovery = TopologyDiscoveryResult.Success([]);
        await controller.RefreshAsync(CancellationToken.None);
        harness.Payments.ReleaseSubmission.SetResult();
        await payment;

        var evidence = controller.State.Evidence.Last(record => record.Kind == EvidenceKind.Payment);
        evidence.Profile.Should().Be(TopologyProfile.Regular);
        evidence.RunGeneration.Should().Be(1);
        controller.State.LastPayment.Should().BeNull();
    }

    [Fact]
    public async Task AsyncLoad_CapturesOldProvenanceAndDoesNotRestoreOldProofAfterDisappearance()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.LoadTests));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.LoadTests, CancellationToken.None);
        harness.Load.RunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Load.ReleaseRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var load = controller.RunLoadTestAsync(100, CancellationToken.None);
        await harness.Load.RunStarted.Task;
        harness.Aspire.Queue(TopologySnapshot.Unreachable(TopologyProfile.LoadTests, harness.Time.GetUtcNow(), "gone"));
        harness.Aspire.Discovery = TopologyDiscoveryResult.Success([]);
        await controller.RefreshAsync(CancellationToken.None);
        harness.Load.ReleaseRun.SetResult();
        await load;

        var evidence = controller.State.Evidence.Last(record => record.Kind == EvidenceKind.LoadTest);
        evidence.Profile.Should().Be(TopologyProfile.LoadTests);
        evidence.RunGeneration.Should().Be(1);
        controller.State.LastLoadResult.Should().BeNull();
    }

    [Fact]
    public async Task StaleRefreshResultAfterSwitch_IsDiscarded()
    {
        var harness = new OperatorHarness();
        var delayed = new DelayedFirstSnapshotAspireAdapter(
            OperatorHarness.Snapshot(TopologyProfile.Regular),
            OperatorHarness.Snapshot(TopologyProfile.LoadTests));
        harness.Preflight.Report = FakePreflightRunner.ReadyReport();
        var controller = new OperatorConsoleController(
            delayed,
            harness.Processes,
            harness.Payments,
            harness.Load,
            harness.Exporter,
            harness.Faults,
            harness.Browser,
            harness.Preflight,
            harness.Feed,
            harness.Time,
            new OperatorConsoleOptions { PollInterval = TimeSpan.Zero, TransitionTimeout = TimeSpan.FromSeconds(1) });
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        var refresh = controller.RefreshAsync(CancellationToken.None);
        await delayed.DelayedCallStarted.Task;
        await controller.SwitchAsync(TopologyProfile.LoadTests, CancellationToken.None);
        delayed.ReleaseDelayedCall.SetResult();
        await refresh;

        controller.State.Profile.Should().Be(TopologyProfile.LoadTests);
        controller.State.Topology!.Profile.Should().Be(TopologyProfile.LoadTests);
    }

    [Fact]
    public async Task NewController_DoesNotRestorePriorEvidence()
    {
        var firstHarness = new OperatorHarness();
        var first = firstHarness.CreateController();
        firstHarness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        await first.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        await first.QueryOutcomeAsync("id", CancellationToken.None);

        var second = new OperatorHarness().CreateController();

        second.State.Evidence.Should().BeEmpty();
        second.State.LastLoadResult.Should().BeNull();
    }

    [Fact]
    public async Task LoadWorkflow_RequiresLoadTestsAndSurfacesAllEvidence()
    {
        var regular = await AttachedControllerAsync(TopologyProfile.Regular);
        var rejected = await regular.Controller.RunLoadTestAsync(100, CancellationToken.None);

        var load = await AttachedControllerAsync(TopologyProfile.LoadTests);
        var accepted = await load.Controller.RunLoadTestAsync(100, CancellationToken.None);

        rejected.Completed.Should().BeFalse();
        accepted.AllPassed.Should().BeTrue();
        accepted.Invariants.Should().HaveCount(5);
        accepted.InlineSettlement.Observed.Should().BeTrue();
        load.Controller.State.LoadProgress.Phase.Should().Be(LoadWorkflowPhase.Completed);
    }

    [Fact]
    public async Task LoadWorkflow_RejectsMissingExpectedCountAndStaleTopology()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.LoadTests);

        var missing = await controller.RunLoadTestAsync(null, CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromSeconds(6));
        var stale = await controller.RunLoadTestAsync(100, CancellationToken.None);

        missing.Completed.Should().BeFalse();
        missing.ErrorSummary.Should().Contain("positive");
        stale.Completed.Should().BeFalse();
        stale.ErrorSummary.Should().Contain("fresh");
    }

    [Fact]
    public async Task Switch_ClearsRetryAndLoadPresentationStateForNewGeneration()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(
            OperatorHarness.Snapshot(TopologyProfile.LoadTests),
            OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.LoadTests, CancellationToken.None);
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        await controller.RunLoadTestAsync(100, CancellationToken.None);

        await controller.SwitchAsync(TopologyProfile.Regular, CancellationToken.None);

        controller.State.LastPayment.Should().BeNull();
        controller.State.CanResendLastPayment.Should().BeFalse();
        controller.State.LastLoadResult.Should().BeNull();
        controller.State.Evidence.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExportAndKnownLinks_AreExplicitAndAllowListed()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        await controller.QueryOutcomeAsync("id", CancellationToken.None);

        var export = await controller.ExportEvidenceAsync(CancellationToken.None);
        var opened = await controller.OpenKnownLinkAsync(KnownLinks.Jaeger, CancellationToken.None);
        var blocked = await controller.OpenKnownLinkAsync("https://arbitrary.example", CancellationToken.None);

        export.Succeeded.Should().BeTrue();
        harness.Exporter.Exported.Should().NotBeEmpty();
        opened.Succeeded.Should().BeTrue();
        blocked.Succeeded.Should().BeFalse();
        blocked.Url.Should().BeNull();
        harness.Browser.Opened.Should().ContainSingle().Which.Should().Be(KnownLinks.Jaeger);
        harness.Browser.VerifiedUrls.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task Evidence_IsBoundedAndSelectionIsExplicit()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController(new OperatorConsoleOptions
        {
            MaximumEvidenceRecords = 2,
            PollInterval = TimeSpan.Zero,
            TransitionTimeout = TimeSpan.FromSeconds(1),
        });
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);

        await controller.QueryOutcomeAsync("one", CancellationToken.None);
        await controller.QueryOutcomeAsync("two", CancellationToken.None);
        await controller.QueryOutcomeAsync("three", CancellationToken.None);
        controller.SelectEvidence(controller.State.Evidence.First().Sequence);

        controller.State.Evidence.Should().HaveCount(2);
        controller.State.SelectedEvidence.Should().Be(controller.State.Evidence.First());
    }

    [Fact]
    public async Task Shutdown_StopsOwnedButNeverAttachedTopology()
    {
        var ownedHarness = new OperatorHarness();
        ownedHarness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var owned = ownedHarness.CreateController();
        await owned.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        await owned.ShutdownAsync(CancellationToken.None);

        var attached = await AttachedControllerAsync(TopologyProfile.Regular);
        await attached.Controller.ShutdownAsync(CancellationToken.None);

        ownedHarness.Processes.StopCount.Should().Be(1);
        attached.Harness.Processes.StopCount.Should().Be(0);
    }

    [Fact]
    public async Task Shutdown_WaitsForActiveOperationBeforeStoppingOwnedInfrastructure()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.SubmissionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.ReleaseSubmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var operationCts = new CancellationTokenSource();

        var payment = controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, operationCts.Token);
        await harness.Payments.SubmissionStarted.Task;
        var shutdown = controller.ShutdownAsync(CancellationToken.None);
        shutdown.IsCompleted.Should().BeFalse();
        harness.Processes.StopCount.Should().Be(0);

        operationCts.Cancel();
        await payment.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
        await shutdown;

        harness.Processes.StopCount.Should().Be(1);
    }

    [Fact]
    public async Task Shutdown_CancelsActiveBurstBeforeStoppingOwnedInfrastructure()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.SubmissionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.ReleaseSubmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var burst = controller.RunBurstAsync(StandardPayment, 5, 1, CancellationToken.None);
        await harness.Payments.SubmissionStarted.Task;
        var shutdown = controller.ShutdownAsync(CancellationToken.None);

        await burst;
        await shutdown;

        controller.State.Burst.Cancelled.Should().BeTrue();
        harness.Processes.StopCount.Should().Be(1);
    }

    [Fact]
    public async Task WorkspaceSelection_RaisesStateChange()
    {
        var controller = new OperatorHarness().CreateController();
        OperatorConsoleState? observed = null;
        controller.StateChanged += state => observed = state;

        controller.SelectWorkspace(WorkspaceKind.LoadTest);

        controller.State.ActiveWorkspace.Should().Be(WorkspaceKind.LoadTest);
        observed.Should().Be(controller.State);
    }


    // --- Outcome feedback loop -----------------------------------------------------------
    //
    // These are the honesty rules and they are the point of the feature: silence is never an
    // outcome, a contradiction is shown rather than resolved, and the console never claims to
    // be waiting for an answer nobody is listening for.

    private static readonly DateTimeOffset ProcessedAt = new(2026, 8, 29, 12, 4, 31, 882, TimeSpan.Zero);

    [Fact]
    public async Task Settlement_CompletedAndBothLegs_ResolvesTheRowWithTwoClocksAndAlignedLegs()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202, "Pending"));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromMilliseconds(222));

        harness.Feed.PushCompleted("transaction-id", ProcessedAt);
        harness.Feed.PushBalance("transaction-id", "1001", -250m, 4750m);
        harness.Feed.PushBalance("transaction-id", "2002", 250m, 1180m);

        var row = controller.State.TrackedPayments.Single();
        row.State.Should().Be(PaymentTrackingState.Settled);
        row.ProcessedAt.Should().Be(ProcessedAt);
        row.ObservedAt.Should().Be(harness.Time.GetUtcNow(), "the console's own clock is a separate figure from the event's");
        row.ObservedLegs.Should().HaveCount(2);
        row.ObservedLegs.Select(leg => leg.AccountNumber).Should().Equal("1001", "2002");
    }

    [Fact]
    public async Task Rejection_FailedEvent_CarriesTheFullErrorReasonAndNoLegs()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202, "Pending"));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        harness.Feed.PushFailed("transaction-id", ProcessedAt, "insufficient funds");

        var row = controller.State.TrackedPayments.Single();
        row.State.Should().Be(PaymentTrackingState.Rejected);
        row.ErrorReason.Should().Be("insufficient funds");
        row.ObservedLegs.Should().BeEmpty();
        controller.State.Evidence.Should().Contain(record =>
            record.Kind == EvidenceKind.OutcomeEvent && record.Summary.Contains("insufficient funds"));
    }

    [Fact]
    public async Task OneLegOnly_IsNeverInferredAsComplete()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202, "Pending"));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        harness.Feed.PushCompleted("transaction-id", ProcessedAt);
        harness.Feed.PushBalance("transaction-id", "1001", -250m, 4750m);

        controller.State.TrackedPayments.Single().ObservedLegs.Should().HaveCount(1);
    }

    [Fact]
    public async Task RedeliveredEvents_AreIdempotent()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202, "Pending"));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        harness.Feed.PushCompleted("transaction-id", ProcessedAt);
        harness.Feed.PushCompleted("transaction-id", ProcessedAt);
        harness.Feed.PushBalance("transaction-id", "1001", -250m, 4750m);
        harness.Feed.PushBalance("transaction-id", "1001", -250m, 4750m);

        var row = controller.State.TrackedPayments.Single();
        row.State.Should().Be(PaymentTrackingState.Settled);
        row.ObservedLegs.Should().HaveCount(1, "at-least-once delivery means a second copy of a leg is not a second leg");
    }

    [Fact]
    public async Task UnattributedEvent_IsRecordedLabelledAndTouchesNoPaymentRow()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202, "Pending"));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        harness.Feed.PushCompleted("tx-9004", ProcessedAt);

        controller.State.TrackedPayments.Single().State.Should().Be(PaymentTrackingState.Awaiting);
        controller.State.Burst.Settled.Should().Be(0, "an unattributed event never counts toward the burst's proven leg");
        controller.State.Evidence.Should().Contain(record =>
            record.Kind == EvidenceKind.OutcomeEvent
            && record.Summary.Contains("Unattributed")
            && record.Summary.Contains("tx-9004"));
    }

    [Fact]
    public async Task Contradiction_KeepsBothRecordsAndPicksNoWinner()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Completed, 200, "Completed"));
        await controller.SubmitPaymentAsync(InstantPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        harness.Feed.PushFailed("transaction-id", ProcessedAt, "insufficient funds");

        var row = controller.State.TrackedPayments.Single();
        row.State.Should().Be(PaymentTrackingState.Contradiction);
        row.HttpOutcome.Should().Be(PaymentOutcome.Completed, "the HTTP record is never overwritten");
        row.BroadcastOutcome.Should().Be(PaymentOutcome.Failed);
        row.Note.Should().Contain("HTTP proved Completed, broadcast says Failed");
    }

    [Fact]
    public async Task FeedLost_WithdrawsEveryAwaitingClaimAndAnnouncesOnce()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(
            Payment(PaymentOutcome.Pending, 202, "Pending") with { TransactionId = "tx-1" },
            Payment(PaymentOutcome.Pending, 202, "Pending") with { TransactionId = "tx-2" });
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        var lostAt = harness.Time.GetUtcNow();

        harness.Feed.Fault(lostAt);

        controller.State.TrackedPayments.Should().OnlyContain(payment =>
            payment.State == PaymentTrackingState.OutcomeUnknown);
        controller.State.TrackedPayments.Should().NotContain(payment =>
            payment.State == PaymentTrackingState.Awaiting);
        controller.State.Evidence.Count(record => record.Summary.StartsWith("Feed lost")).Should().Be(1);
        controller.State.Evidence.Should().Contain(record =>
            record.Summary.Contains("Feed lost") && record.Summary.Contains("2 payments"));
    }

    [Fact]
    public async Task FeedResumed_StampsTheGapAndNeverBackFillsAnUnknownRow()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202, "Pending"));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        harness.Feed.Fault(harness.Time.GetUtcNow());
        harness.Time.Advance(TimeSpan.FromSeconds(17));

        harness.Feed.Resume(harness.Time.GetUtcNow());

        controller.State.Feed.State.Should().Be(OutcomeFeedState.Listening);
        controller.State.Feed.GapStart.Should().NotBeNull();
        controller.State.TrackedPayments.Single().State.Should().Be(
            PaymentTrackingState.OutcomeUnknown,
            "a resumed subscription is not retroactive evidence");
        controller.State.Evidence.Should().Contain(record => record.Summary.Contains("Listening again"));
    }

    [Fact]
    public async Task SidecarUnavailable_StillStartsTheTopologyAndNamesTheRemedy()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        harness.Feed.QueueUnavailable("daprd is not on PATH");
        var controller = harness.CreateController();

        var start = await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        start.Succeeded.Should().BeTrue("a missing feed must never block a topology or a payment");
        controller.State.Feed.State.Should().Be(OutcomeFeedState.Unavailable);
        controller.State.Feed.Detail.Should().Contain("daprd is not on PATH");
        controller.State.Evidence.Should().Contain(record =>
            record.Kind == EvidenceKind.OutcomeEvent && record.Summary.Contains("Query outcome"));
    }

    [Fact]
    public async Task NeverListeningSubmit_StartsAtOutcomeNotObservedRatherThanAwaiting()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        harness.Feed.QueueUnavailable("daprd is not on PATH");
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202, "Pending"));

        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        var row = controller.State.TrackedPayments.Single();
        row.State.Should().Be(PaymentTrackingState.NotObserved);
        row.Note.Should().Contain("daprd is not on PATH");
    }

    [Fact]
    public async Task AwaitingPayment_IsNeverConvertedToAFailureByElapsedTime()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202, "Pending"));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        harness.Time.Advance(TimeSpan.FromHours(1));
        await controller.RefreshAsync(CancellationToken.None);

        controller.State.TrackedPayments.Single().State.Should().Be(PaymentTrackingState.Awaiting);
    }

    [Fact]
    public async Task ArrivingEvent_ResolvesInPlaceWithoutReorderingTheList()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(
            Payment(PaymentOutcome.Pending, 202, "Pending") with { TransactionId = "tx-1" },
            Payment(PaymentOutcome.Pending, 202, "Pending") with { TransactionId = "tx-2" },
            Payment(PaymentOutcome.Pending, 202, "Pending") with { TransactionId = "tx-3" });
        for (var index = 0; index < 3; index++)
        {
            await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        }

        harness.Feed.PushCompleted("tx-1", ProcessedAt);

        // An arriving outcome never re-sorts the list.
        controller.State.TrackedPayments.Select(payment => payment.TransactionId)
            .Should().Equal("tx-1", "tx-2", "tx-3");
        controller.State.TrackedPayments[0].State.Should().Be(PaymentTrackingState.Settled);
        controller.State.SelectedEvidence!.Kind.Should().NotBe(
            EvidenceKind.OutcomeEvent,
            "a pushed outcome never steals the Details pane");
    }

    [Fact]
    public async Task Burst_ProvenLegMovesOnlyOnReceivedEventsAndAwaitingDrainsToZero()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);

        await controller.RunBurstAsync(StandardPayment, 2, 1, CancellationToken.None);

        controller.State.Burst.Accepted.Should().Be(2);
        controller.State.Burst.Settled.Should().Be(0);
        controller.State.Burst.Awaiting.Should().Be(2, "awaiting goes up as submissions are accepted");

        var burstTransactions = harness.Payments.Submissions
            .Select(submission => submission.IdempotencyKey!)
            .ToList();
        harness.Feed.PushCompleted(burstTransactions[0], ProcessedAt);
        harness.Feed.PushFailed(burstTransactions[1], ProcessedAt, "insufficient funds");

        controller.State.Burst.Settled.Should().Be(1);
        controller.State.Burst.Rejected.Should().Be(1);
        controller.State.Burst.Awaiting.Should().Be(0, "awaiting only ever drains from received events");
        controller.State.TrackedPayments.Should().BeEmpty("a burst's outcomes are counted, never followed row by row");
    }

    [Fact]
    public async Task Burst_RedeliveredTerminalEvent_DoesNotDoubleCountTheProvenLeg()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        await controller.RunBurstAsync(StandardPayment, 1, 1, CancellationToken.None);
        var transactionId = harness.Payments.Submissions[0].IdempotencyKey!;

        harness.Feed.PushCompleted(transactionId, ProcessedAt);
        harness.Feed.PushCompleted(transactionId, ProcessedAt);

        controller.State.Burst.Settled.Should().Be(1);
    }

    [Fact]
    public async Task LateEvent_AfterTheTopologyStops_IsDiscarded()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202, "Pending"));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        await controller.StopAsync(CancellationToken.None);
        var evidenceBefore = controller.State.Evidence.Count;

        harness.Feed.PushCompleted("transaction-id", ProcessedAt);

        controller.State.Evidence.Should().HaveCount(evidenceBefore);
        controller.State.TrackedPayments.Should().BeEmpty();
    }

    [Fact]
    public async Task Feed_IsTornDownWithTheTopologyOnStopSwitchShutdownAndDisappearance()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(
            OperatorHarness.Snapshot(TopologyProfile.Regular),
            OperatorHarness.Snapshot(TopologyProfile.LoadTests));
        var controller = harness.CreateController();

        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Feed.Starts.Should().Equal(TopologyProfile.Regular);

        await controller.SwitchAsync(TopologyProfile.LoadTests, CancellationToken.None);
        harness.Feed.StopCount.Should().Be(1, "the outgoing half of a switch owns the outgoing sidecar");
        harness.Feed.Starts.Should().Equal(TopologyProfile.Regular, TopologyProfile.LoadTests);

        await controller.ShutdownAsync(CancellationToken.None);
        harness.Feed.StopCount.Should().Be(2, "no orphan daprd may outlive the console");
    }

    [Fact]
    public async Task Feed_IsTornDownWhenTheOwnedAppHostDisappears()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Aspire.DefaultSnapshot = TopologySnapshot.Unreachable(
            TopologyProfile.Regular,
            harness.Time.GetUtcNow(),
            "gone");
        harness.Aspire.Discovered = [];

        await controller.RefreshAsync(CancellationToken.None);

        controller.State.Profile.Should().Be(TopologyProfile.None);
        harness.Feed.StopCount.Should().Be(1);
        controller.State.Feed.State.Should().Be(OutcomeFeedState.NotStarted);
    }

    [Fact]
    public async Task BufferedEventsOverflow_KeepsTheNewestAndDropsTheOldest()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);

        // One past the cap. The buffer exists so an event that beat its own HTTP response still
        // lands, so the entry worth keeping under pressure is the newest — it is the one most
        // likely to still have a submission on the way.
        var overflow = OperatorConsoleController.MaximumUnmatchedTerminalEvents + 1;
        for (var index = 0; index < overflow; index++)
        {
            harness.Feed.PushCompleted($"tx-buffer-{index}", ProcessedAt);
        }

        harness.Payments.Queue(
            Payment(PaymentOutcome.Completed, 200, "Completed") with { TransactionId = "tx-buffer-0" },
            Payment(PaymentOutcome.Completed, 200, "Completed") with { TransactionId = $"tx-buffer-{overflow - 1}" });

        await controller.SubmitPaymentAsync(InstantPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        await controller.SubmitPaymentAsync(InstantPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        var rows = controller.State.TrackedPayments;
        rows.Single(row => row.TransactionId == "tx-buffer-0").State
            .Should().NotBe(PaymentTrackingState.Settled, "the oldest buffered event is the one evicted");
        rows.Single(row => row.TransactionId == $"tx-buffer-{overflow - 1}").State
            .Should().Be(PaymentTrackingState.Settled, "the newest buffered event survives the overflow");
    }

    [Fact]
    public async Task EventArrivingBeforeItsOwnSubmissionResponse_StillResolvesTheRow()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Completed, 200, "Completed"));

        // The instant rail commits before its own 200 returns, so the broadcast can genuinely
        // beat the HTTP response. Correlating it is within-session, not replay.
        harness.Feed.PushCompleted("transaction-id", ProcessedAt);
        await controller.SubmitPaymentAsync(InstantPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        controller.State.TrackedPayments.Single().State.Should().Be(PaymentTrackingState.Settled);
        controller.State.Evidence.Should().Contain(record => record.Summary.Contains("attributed"));
    }

    [Fact]
    public async Task PaymentEvidence_CarriesTheTransactionIdItCorrelatesBy()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202, "Pending"));

        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        harness.Feed.PushCompleted("transaction-id", ProcessedAt);

        controller.State.Evidence.Where(record => record.TransactionId == "transaction-id")
            .Should().HaveCount(2, "the submission and its broadcast outcome share the one correlation id");
    }


    [Fact]
    public async Task DroppedFeed_IsReestablishedOnRefreshAndStampsTheGapWithoutBackFilling()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202, "Pending"));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        harness.Feed.Fault(harness.Time.GetUtcNow());
        harness.Time.Advance(TimeSpan.FromSeconds(17));
        harness.Feed.Queue(new OutcomeFeedStatus(
            OutcomeFeedState.Listening,
            ListeningSince: harness.Time.GetUtcNow(),
            GapStart: harness.Time.GetUtcNow().AddSeconds(-17),
            GapEnd: harness.Time.GetUtcNow()));

        await controller.RefreshAsync(CancellationToken.None);

        harness.Feed.Starts.Should().HaveCount(2, "a subscription that dropped is re-established while the topology runs");
        controller.State.Feed.State.Should().Be(OutcomeFeedState.Listening);
        controller.State.TrackedPayments.Single().State.Should().Be(PaymentTrackingState.OutcomeUnknown);
    }

    [Fact]
    public async Task FeedThatNeverCameUp_IsNotRetriedOnEveryPoll()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        harness.Feed.QueueUnavailable("daprd is not on PATH");
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        for (var poll = 0; poll < 5; poll++)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(30));
            await controller.RefreshAsync(CancellationToken.None);
        }

        harness.Feed.Starts.Should().HaveCount(1, "a cause that does not fix itself must not spam the evidence feed");
    }


    [Fact]
    public async Task DroppedFeed_IsNotRetriedMoreThanOncePerInterval()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Feed.Fault(harness.Time.GetUtcNow());
        // Every start respawns a sidecar. Without the interval the 1.5-second poll would spawn
        // a fresh daprd several times a second for as long as the outage lasted.
        harness.Feed.Queue(new OutcomeFeedStatus(OutcomeFeedState.Lost, LostAt: harness.Time.GetUtcNow()));

        for (var poll = 0; poll < 5; poll++)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(1));
            await controller.RefreshAsync(CancellationToken.None);
            await controller.FeedReconnectInFlight;
        }

        harness.Feed.Starts.Should().HaveCount(2, "one initial start plus a single retry inside the interval");
    }

    [Fact]
    public async Task DroppedFeed_StopsRetryingAfterTheCapAndSaysSoOnce()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Feed.Fault(harness.Time.GetUtcNow());
        for (var attempt = 0; attempt < 10; attempt++)
        {
            harness.Feed.Queue(new OutcomeFeedStatus(OutcomeFeedState.Lost, LostAt: harness.Time.GetUtcNow()));
        }

        for (var poll = 0; poll < 10; poll++)
        {
            harness.Time.Advance(TimeSpan.FromMinutes(1));
            await controller.RefreshAsync(CancellationToken.None);
            await controller.FeedReconnectInFlight;
        }

        harness.Feed.Starts.Should().HaveCount(4, "one initial start plus a bounded three retries");
        controller.State.Evidence.Count(record => record.Summary.Contains("stopped retrying"))
            .Should().Be(1, "giving up is announced once, not on every poll");
    }

    [Fact]
    public async Task SuccessfulReconnect_RestoresTheRetryBudgetForTheNextOutage()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        // Three outages, each recovered on the first retry. The cap is on consecutive failures,
        // not on how many outages one session may survive.
        for (var outage = 0; outage < 4; outage++)
        {
            harness.Feed.Fault(harness.Time.GetUtcNow());
            harness.Time.Advance(TimeSpan.FromMinutes(1));
            await controller.RefreshAsync(CancellationToken.None);
            await controller.FeedReconnectInFlight;
            controller.State.Feed.State.Should().Be(OutcomeFeedState.Listening);
        }

        harness.Feed.Starts.Should().HaveCount(5, "one initial start plus one successful reconnect per outage");
    }

    [Fact]
    public async Task TrackedPayments_AreBoundedAndAnEvictedRowsOutcomeIsStillCalledOurs()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController(new OperatorConsoleOptions
        {
            PollInterval = TimeSpan.Zero,
            TransitionTimeout = TimeSpan.FromSeconds(1),
            MaximumTrackedPayments = 3,
        });
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        for (var index = 0; index < 5; index++)
        {
            harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202, "Pending") with { TransactionId = $"tx-{index}" });
            await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        }

        controller.State.TrackedPayments.Should().HaveCount(3);
        controller.State.TrackedPayments.Select(row => row.TransactionId).Should().Equal("tx-2", "tx-3", "tx-4");

        harness.Feed.PushCompleted("tx-0", ProcessedAt);

        controller.State.Evidence.Should().Contain(record =>
            record.TransactionId == "tx-0" && record.Summary.Contains("submitted earlier this session"));
        controller.State.Evidence.Should().NotContain(record =>
            record.TransactionId == "tx-0" && record.Summary.Contains("was not submitted from this console"));
    }

    [Fact]
    public async Task Resend_UpdatesTheExistingRowRatherThanAddingOneThatCanNeverResolve()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        // The instant rail may answer 202 first and 200 on the resend, and both are legal for
        // it -- so the row must absorb the second answer rather than gaining a twin that could
        // never resolve and would read "Awaiting settlement" for ever.
        harness.Payments.Queue(
            Payment(PaymentOutcome.Pending, 202, "Pending"),
            Payment(PaymentOutcome.Completed, 200, "Completed"));

        await controller.SubmitPaymentAsync(InstantPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        await controller.ResendLastPaymentAsync(CancellationToken.None);

        var row = controller.State.TrackedPayments.Should().ContainSingle().Subject;
        row.HttpOutcome.Should().Be(PaymentOutcome.Completed);
        row.HttpStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task PreviousBurstsOutcome_IsNeverCalledAStrangersTransaction()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        await controller.RunBurstAsync(StandardPayment, 1, 1, CancellationToken.None);
        var firstBurstTransaction = harness.Payments.Submissions[0].IdempotencyKey!;
        await controller.RunBurstAsync(StandardPayment, 1, 1, CancellationToken.None);

        harness.Feed.PushCompleted(firstBurstTransaction, ProcessedAt);

        controller.State.Evidence.Should().NotContain(record =>
            record.TransactionId == firstBurstTransaction
            && record.Summary.Contains("was not submitted from this console"));
        controller.State.Burst.Settled.Should().Be(0, "a previous burst's outcome never moves this burst's counters");
    }

    [Fact]
    public async Task Burst_WhenTheFeedDrops_WithdrawsItsAwaitingClaimToo()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        await controller.RunBurstAsync(StandardPayment, 3, 1, CancellationToken.None);
        controller.State.Burst.Awaiting.Should().Be(3);

        harness.Feed.Fault(harness.Time.GetUtcNow());

        controller.State.Burst.Awaiting.Should().Be(
            0,
            "leaving 'awaiting 3' on screen with nobody listening is the same false wait the rows withdraw");
        controller.State.Burst.Unknown.Should().Be(3);
    }

    [Fact]
    public async Task HttpFailedBurstSubmission_NeverDrivesTheProvenLegNegative()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Failed, 200, "Failed") with { TransactionId = "tx-failed" });

        await controller.RunBurstAsync(InstantPayment, 1, 1, CancellationToken.None);
        harness.Feed.PushFailed("tx-failed", ProcessedAt, "insufficient funds");

        controller.State.Burst.Failed.Should().Be(1, "the HTTP leg counted it as failed");
        controller.State.Burst.Rejected.Should().Be(0, "its id never joined the proven leg, so nothing can go negative");
        controller.State.Burst.Awaiting.Should().Be(0);
        controller.State.Evidence.Should().NotContain(record =>
            record.TransactionId == "tx-failed" && record.Summary.Contains("was not submitted from this console"));
    }

    [Fact]
    public async Task BalanceLegsArrivingBeforeTheSubmissionResponse_AreStillApplied()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Completed, 200, "Completed"));

        // All three events can beat an instant-rail 200. Buffering only the terminal one left
        // the row permanently reading "1 of 2 legs observed".
        harness.Feed.PushCompleted("transaction-id", ProcessedAt);
        harness.Feed.PushBalance("transaction-id", "1001", -250m, 4750m);
        harness.Feed.PushBalance("transaction-id", "2002", 250m, 1180m);
        await controller.SubmitPaymentAsync(InstantPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        var row = controller.State.TrackedPayments.Single();
        row.State.Should().Be(PaymentTrackingState.Settled);
        row.ObservedLegs.Should().HaveCount(2);
    }

    [Fact]
    public async Task AmbiguousSubmissionWithATransactionId_IsTrackedAtOutcomeNotObserved()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Ambiguous, 0, null, "no response"));

        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Omitted, null, CancellationToken.None);

        var row = controller.State.TrackedPayments.Should().ContainSingle().Subject;
        row.State.Should().Be(
            PaymentTrackingState.NotObserved,
            "its own HTTP leg never proved it was accepted, so it is never 'Awaiting settlement'");
        row.HttpOutcome.Should().Be(PaymentOutcome.Ambiguous);
        row.Note.Should().Contain("Ambiguous");
    }

    [Fact]
    public async Task BalanceLegForARejectedRow_IsNotRenderedBesideTheWordsThatDenyIt()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202, "Pending"));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        harness.Feed.PushFailed("transaction-id", ProcessedAt, "insufficient funds");
        harness.Feed.PushBalance("transaction-id", "1001", -250m, 4750m);

        controller.State.TrackedPayments.Single().ObservedLegs.Should().BeEmpty();
        controller.State.Evidence.Should().Contain(record =>
            record.Method == OutcomeEventTypes.BalanceUpdated,
            "the event is still recorded in the feed, where the disagreement is visible");
    }

    [Fact]
    public async Task InboundEvent_NeverRewritesTheMutationStatusLine()
    {
        var (controller, harness) = await AttachedControllerAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202, "Pending"));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        var statusLine = controller.State.StatusLine;

        harness.Feed.PushBalance("tx-9004", "1001", -250m, 4750m);

        controller.State.StatusLine.Should().Be(statusLine, "a pushed outcome never steals attention");
        controller.State.Evidence.Last().Summary.Should().Contain("Balance updated");
    }

    [Fact]
    public async Task InboundEvent_IsStampedWithTheFaultLevelsInForceWhenItArrived()
    {
        var harness = new OperatorHarness();
        harness.Aspire.DefaultSnapshot = OperatorHarness.ArmedSnapshot(TopologyProfile.Regular);
        var controller = harness.CreateController();
        controller.SetArming(true).Succeeded.Should().BeTrue();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202, "Pending"));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);

        // Armed after the subscription was established. The levels are read when the event
        // lands, not when the console subscribed, or every such record would file as fault-free.
        controller.StageFaults(FaultLevels.AllZero with { ErrorRatePercent = 40 });
        await controller.ApplyFaultsAsync(CancellationToken.None);
        harness.Feed.PushCompleted("transaction-id", ProcessedAt);

        controller.State.Evidence.Last(record => record.Kind == EvidenceKind.OutcomeEvent)
            .FaultLevels.Should().NotBeNull("a settlement observed under injected faults is a different fact");
    }

    private static async Task<(OperatorConsoleController Controller, OperatorHarness Harness)> AttachedControllerAsync(
        TopologyProfile profile)
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(profile));
        var controller = harness.CreateController();
        var result = await controller.AttachAsync(profile, CancellationToken.None);
        result.Succeeded.Should().BeTrue();
        return (controller, harness);
    }

    private static PaymentResult Payment(
        PaymentOutcome outcome,
        int statusCode,
        string? responseStatus,
        string? error = null) =>
        new(
            outcome,
            statusCode,
            "payment-id",
            "transaction-id",
            responseStatus,
            "{}",
            error,
            TimeSpan.FromMilliseconds(5));

    private sealed class DelayedFirstSnapshotAspireAdapter(
        TopologySnapshot regular,
        TopologySnapshot load) : IAspireAdapter
    {
        private int _calls;
        public TaskCompletionSource DelayedCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDelayedCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TopologyDiscoveryResult> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult(TopologyDiscoveryResult.Success([]));

        public async Task<TopologySnapshot> GetSnapshotAsync(TopologyProfile profile, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 2)
            {
                DelayedCallStarted.SetResult();
                await ReleaseDelayedCall.Task.WaitAsync(ct);
                return regular;
            }

            return profile == TopologyProfile.Regular ? regular : load;
        }

        public Task<ResourceCommandResult> ExecuteResourceCommandAsync(
            TopologyProfile profile,
            string resourceName,
            ResourceCommand command,
            CancellationToken ct) =>
            Task.FromResult(ResourceCommandResult.Rejected("not used"));
    }
}
