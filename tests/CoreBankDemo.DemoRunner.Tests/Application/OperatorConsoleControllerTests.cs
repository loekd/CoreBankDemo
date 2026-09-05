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
        controller.State.Evidence.Single().Summary.Should().Contain("Started");
        controller.State.Evidence.Single().Method.Should().StartWith("aspire start --apphost");
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
            harness.Browser,
            harness.Preflight,
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
