using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application;

/// <summary>
/// The recovery path end to end: stop a resource from the console, keep the authority that
/// starts it again, and keep believing the bank while its shape is the one the operator asked
/// for. Shape is not identity — profile plus run generation is.
/// </summary>
public class OperatorConsoleResourceLifecycleTests
{
    private static readonly PaymentRequest StandardPayment =
        new("NL91ABNA0417164300", "NL20INGB0001234567", 10m, "EUR", PaymentRail.Standard);

    private static readonly DateTimeOffset ProcessedAt = new(2026, 9, 10, 13, 38, 16, TimeSpan.Zero);

    [Fact]
    public async Task Stop_ThatMovesTheShape_IsConfirmedAndKeepsResourceAuthority()
    {
        var (controller, harness) = await AttachedAsync(TopologyProfile.Regular);
        harness.Aspire.CommandResult = new ResourceCommandResult(
            ResourceDispatchStatus.Dispatched,
            "both dispatched",
            ["corebank-api-1", "corebank-api-2"],
            []);
        harness.Aspire.DefaultSnapshot = StoppedCoreBankSnapshot(harness);

        var result = await controller.ExecuteResourceCommandAsync(
            KnownResources.CoreBankApi,
            ResourceCommand.Stop,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue("a stop that reached Stopped is confirmed, not ambiguous");
        result.Message.Should().Contain("confirmed");
        controller.State.Evidence.Last().Succeeded.Should().BeTrue();
        controller.State.Evidence.Last().Summary.Should().Contain("Stop corebank-api confirmed");
        controller.State.ResourceAuthorityAvailable.Should().BeTrue(
            "the operator's own stop must not cost them the button that undoes it");
    }

    [Fact]
    public async Task Start_AfterAStopThatMovedTheShape_IsAcceptedAndConfirmed()
    {
        var (controller, harness) = await AttachedAsync(TopologyProfile.Regular);
        harness.Aspire.Queue(StoppedCoreBankSnapshot(harness));
        await controller.RefreshAsync(CancellationToken.None);
        harness.Aspire.CommandResult = new ResourceCommandResult(
            ResourceDispatchStatus.Dispatched,
            "both dispatched",
            ["corebank-api-1", "corebank-api-2"],
            []);
        harness.Aspire.DefaultSnapshot = OperatorHarness.Snapshot(TopologyProfile.Regular, harness.Time.GetUtcNow());

        var result = await controller.ExecuteResourceCommandAsync(
            KnownResources.CoreBankApi,
            ResourceCommand.Start,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        harness.Aspire.Commands.Should().ContainSingle()
            .Which.Should().Be((TopologyProfile.Regular, KnownResources.CoreBankApi, ResourceCommand.Start));
        controller.State.Evidence.Last().Method.Should().Contain("corebank-api-1 start")
            .And.Contain("corebank-api-2 start");
    }

    [Fact]
    public async Task SettlementBroadcast_WhileTheShapeIsMismatched_ResolvesItsRowAndReachesEvidence()
    {
        var (controller, harness) = await AttachedAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Pending());
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        harness.Aspire.Queue(StoppedCoreBankSnapshot(harness));
        await controller.RefreshAsync(CancellationToken.None);
        controller.State.Topology!.IsFingerprintMatch.Should().BeFalse("the stopped bank moved the shape");

        harness.Feed.PushCompleted("transaction-id", ProcessedAt);

        controller.State.TrackedPayments.Single().State.Should().Be(PaymentTrackingState.Settled);
        controller.State.Evidence.Should().Contain(record =>
            record.Kind == EvidenceKind.OutcomeEvent && record.TransactionId == "transaction-id");
    }

    [Fact]
    public async Task PaymentSubmittedWhileStopped_IsStillTrackedAfterTheShapeMovesBack()
    {
        var (controller, harness) = await AttachedAsync(TopologyProfile.Regular);
        harness.Aspire.Queue(StoppedCoreBankSnapshot(harness));
        await controller.RefreshAsync(CancellationToken.None);

        harness.Payments.Queue(Pending());
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular, harness.Time.GetUtcNow()));
        await controller.RefreshAsync(CancellationToken.None);
        harness.Feed.PushCompleted("transaction-id", ProcessedAt);

        var row = controller.State.TrackedPayments.Single();
        row.State.Should().Be(PaymentTrackingState.Settled);
    }

    [Fact]
    public async Task FeedLost_WhileAResourceIsStopped_StillReachesTheOperator()
    {
        var (controller, harness) = await AttachedAsync(TopologyProfile.Regular);
        harness.Payments.Queue(Pending());
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        harness.Aspire.Queue(StoppedCoreBankSnapshot(harness));
        await controller.RefreshAsync(CancellationToken.None);

        harness.Feed.Fault(harness.Time.GetUtcNow());

        controller.State.Feed.State.Should().Be(OutcomeFeedState.Lost);
        controller.State.TrackedPayments.Single().State.Should().Be(PaymentTrackingState.OutcomeUnknown,
            "a row may not keep asserting something the console no longer knows to be true");

        // ... and the subscription stays eligible for re-establishment while the shape is still
        // the one the operator's own Stop left behind.
        harness.Aspire.Queue(StoppedCoreBankSnapshot(harness));
        await controller.RefreshAsync(CancellationToken.None);
        await controller.FeedReconnectInFlight;

        harness.Feed.Starts.Should().HaveCount(2, "one initial start plus the reconnect attempt");
    }

    /// <summary>
    /// What still isolates one run from the next now that shape does not: a topology stop drops
    /// the subscription context outright, and the following start moves the generation, so
    /// nothing from the previous run can be read as current.
    /// </summary>
    [Fact]
    public async Task AfterATopologyStop_ArrivingEventsAreDiscardedAndTheNextRunStartsClean()
    {
        var harness = new OperatorHarness();
        harness.Aspire.DefaultSnapshot = OperatorHarness.Snapshot(TopologyProfile.Regular);
        var controller = harness.CreateController();
        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.Queue(Pending());
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Generated, null, CancellationToken.None);
        var generation = controller.State.RunGeneration;

        await controller.StopAsync(CancellationToken.None);
        harness.Feed.PushCompleted("transaction-id", ProcessedAt);

        controller.State.Evidence.Should().NotContain(record =>
            record.Kind == EvidenceKind.OutcomeEvent && record.TransactionId == "transaction-id");

        await controller.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        controller.State.RunGeneration.Should().BeGreaterThan(generation);
        controller.State.TrackedPayments.Should().BeEmpty("a new generation starts with no payment history");
    }

    [Fact]
    public async Task UnreadableSnapshot_RefusesEveryResourceCommandAndDispatchesNothing()
    {
        var (controller, harness) = await AttachedAsync(TopologyProfile.Regular);
        harness.Aspire.Queue(OperatorHarness.UnreadableSnapshot(TopologyProfile.Regular, harness.Time.GetUtcNow()));
        await controller.RefreshAsync(CancellationToken.None);

        var result = await controller.ExecuteResourceCommandAsync(
            KnownResources.CoreBankApi,
            ResourceCommand.Start,
            CancellationToken.None);

        controller.State.ResourceAuthorityAvailable.Should().BeFalse();
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Refresh state");
        harness.Aspire.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task StaleSnapshot_RefusesResourceCommandsAndNamesRefresh()
    {
        var (controller, harness) = await AttachedAsync(TopologyProfile.Regular);
        harness.Time.Advance(TimeSpan.FromMinutes(5));

        var result = await controller.ExecuteResourceCommandAsync(
            KnownResources.CoreBankApi,
            ResourceCommand.Stop,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Refresh state");
        harness.Aspire.Commands.Should().BeEmpty();
    }

    /// <summary>
    /// <c>ErrorSummary</c> carries two meanings and only one of them still blocks. The parser's
    /// shape-mismatch text does not; the debouncer's hold — "I have not confirmed what I am
    /// looking at" — does, and is pinned here by its own named constant.
    /// </summary>
    [Fact]
    public async Task DebouncerHold_StillBlocksResourceCommands_WhileAShapeMismatchDoesNot()
    {
        var (controller, harness) = await AttachedAsync(TopologyProfile.Regular);
        var held = OperatorHarness.Snapshot(TopologyProfile.Regular, harness.Time.GetUtcNow()) with
        {
            ErrorSummary = TopologyObservationDebouncer.AwaitingConfirmationSummary,
        };
        held.IsAwaitingConfirmation.Should().BeTrue();
        harness.Aspire.Queue(held);
        await controller.RefreshAsync(CancellationToken.None);

        var duringHold = await controller.ExecuteResourceCommandAsync(
            KnownResources.CoreBankApi,
            ResourceCommand.Stop,
            CancellationToken.None);

        harness.Aspire.Queue(StoppedCoreBankSnapshot(harness));
        await controller.RefreshAsync(CancellationToken.None);
        var mismatched = controller.State.Topology!;

        duringHold.Succeeded.Should().BeFalse();
        harness.Aspire.Commands.Should().BeEmpty();
        mismatched.IsFingerprintMatch.Should().BeFalse();
        mismatched.IsAwaitingConfirmation.Should().BeFalse();
        controller.State.ResourceAuthorityAvailable.Should().BeTrue(
            "a shape the console read and understood is not uncertainty");
    }

    [Fact]
    public async Task MismatchedLoadTestsGraph_StillRefusesTheLoadWorkflow()
    {
        var (controller, harness) = await AttachedAsync(TopologyProfile.LoadTests);
        harness.Aspire.Queue(OperatorHarness.Snapshot(
            TopologyProfile.LoadTests,
            harness.Time.GetUtcNow(),
            fingerprint: false));
        await controller.RefreshAsync(CancellationToken.None);

        var result = await controller.RunLoadTestAsync(10, CancellationToken.None);

        controller.CanRunLoadTest.Should().BeFalse("load-test gating goes through IsReady, which is untouched");
        result.Completed.Should().BeFalse();
        result.ErrorSummary.Should().Contain("fresh, verified LoadTests snapshot");
    }

    private static async Task<(OperatorConsoleController Controller, OperatorHarness Harness)> AttachedAsync(
        TopologyProfile profile)
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(profile));
        var controller = harness.CreateController();
        (await controller.AttachAsync(profile, CancellationToken.None)).Succeeded.Should().BeTrue();
        return (controller, harness);
    }

    /// <summary>The graph after the operator stopped both <c>corebank-api</c> replicas.</summary>
    private static TopologySnapshot StoppedCoreBankSnapshot(OperatorHarness harness) =>
        OperatorHarness.Snapshot(
            TopologyProfile.Regular,
            harness.Time.GetUtcNow(),
            fingerprint: false,
            resources:
            [
                new ResourceSnapshot(
                    KnownResources.CoreBankApi,
                    ResourceCondition.Stopped,
                    "Stopped",
                    [],
                    0,
                    InstanceNames: ["corebank-api-1", "corebank-api-2"],
                    AllowedCommands: Enum.GetValues<ResourceCommand>().ToHashSet()),
                .. OperatorHarness.DefaultResources(TopologyProfile.Regular)
                    .Where(resource => resource.Name != KnownResources.CoreBankApi),
            ]);

    private static PaymentResult Pending() =>
        new(
            PaymentOutcome.Pending,
            202,
            "payment-id",
            "transaction-id",
            "Pending",
            "{}",
            null,
            TimeSpan.FromMilliseconds(5));
}
