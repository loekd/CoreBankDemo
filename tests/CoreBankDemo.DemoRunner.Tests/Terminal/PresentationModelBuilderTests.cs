using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Terminal;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

public class PresentationModelBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_EmptyState_ShowsFiveWorkspacesAndColdPlaceholders()
    {
        var model = PresentationModelBuilder.Build(OperatorConsoleState.Empty, Now);

        model.Navigation.Should().HaveCount(5);
        model.Navigation.Should().Contain(item => item.Shortcut == "1" && item.Label == "Operations");
        model.Navigation.Should().Contain(item => item.Shortcut == "5" && item.Label == "Faults");
        model.EvidenceStrip.Should().Be("No actions yet this session.");
        model.LoadResults.Should().HaveCount(6);
        model.LoadResults.Should().OnlyContain(value => value.Contains("not yet observed"));
    }

    [Fact]
    public void Build_ResourceStates_UseSymbolTextAndStableActions()
    {
        var resources = new[]
        {
            new ResourceSnapshot(KnownResources.CoreBankApi, ResourceCondition.Healthy, "Healthy", ["http://core"], 2),
            new ResourceSnapshot(KnownResources.PaymentsApi, ResourceCondition.Stopped, "Stopped", []),
            new ResourceSnapshot(KnownResources.Redis, ResourceCondition.Unreachable, "Unreachable", []),
            new ResourceSnapshot(KnownResources.Postgres, ResourceCondition.Failed, "Failed", []),
        };
        var snapshot = OperatorHarness.Snapshot(TopologyProfile.Regular, resources: resources);
        var state = OperatorConsoleState.Empty with
        {
            Profile = TopologyProfile.Regular,
            Ownership = TopologyOwnership.Attached,
            RunGeneration = 3,
            Topology = snapshot,
            ResourceAuthorityAvailable = true,
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.TopologyBar.Should().Contain("Regular").And.Contain("Attached");
        model.Resources.Should().Contain(row => row.Name == KnownResources.CoreBankApi && row.Symbol == "●" && row.NextAction == "Stop");
        model.Resources.Should().Contain(row => row.Name == KnownResources.PaymentsApi && row.Symbol == "○" && row.NextAction == "Start");
        model.Resources.Should().Contain(row => row.Name == KnownResources.Redis && row.State == "Unreachable" && !row.CanMutate);
        model.Resources.Should().Contain(row => row.Name == KnownResources.Postgres && row.Symbol == "✕" && row.NextAction == "Restart");
    }

    [Fact]
    public void Build_EvidenceAndLoadResults_ShowProvenanceAndIndividualVerdicts()
    {
        var evidence = new EvidenceRecord(
            7,
            DateTimeOffset.UnixEpoch,
            TopologyProfile.LoadTests,
            4,
            EvidenceKind.LoadTest,
            "Load workflow passed",
            "accepted load workflow",
            "load",
            null,
            TimeSpan.FromSeconds(2),
            "raw",
            true);
        var result = LoadWorkflowResult.Success(
            [new InvariantResult("Exactly-once processing", true, "ok")],
            new InlineSettlementResult(true, "count=20"),
            "raw");
        var state = OperatorConsoleState.Empty with
        {
            Profile = TopologyProfile.LoadTests,
            Ownership = TopologyOwnership.Owned,
            RunGeneration = 4,
            Topology = OperatorHarness.Snapshot(TopologyProfile.LoadTests),
            ResourceAuthorityAvailable = true,
            Evidence = [evidence],
            SelectedEvidence = evidence,
            LastLoadResult = result,
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.Evidence.Single().Provenance.Should().Contain("LoadTests · generation 4");
        model.SelectedEvidenceDetail.Should().Contain("raw");
        model.LoadResults.Should().Contain(value => value.Contains("Inline instant settlement"));
        model.CanStopOrSwitch.Should().BeTrue();
        model.CanUseLoadTest.Should().BeTrue();
    }

    [Fact]
    public void Build_ColdState_ExplainsWhyEveryGatedControlIsUnavailable()
    {
        var model = PresentationModelBuilder.Build(OperatorConsoleState.Empty, Now);

        model.OperationsHint.Should().Contain("No topology attached");
        model.ResourcesHint.Should().Contain("Preflight");
        model.LoadHint.Should().Contain("LoadTests topology");
    }

    [Fact]
    public void Build_ReadyLoadTopology_LeavesEveryHintEmpty()
    {
        var state = OperatorConsoleState.Empty with
        {
            Profile = TopologyProfile.LoadTests,
            Ownership = TopologyOwnership.Owned,
            Topology = OperatorHarness.Snapshot(TopologyProfile.LoadTests),
            ResourceAuthorityAvailable = true,
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.OperationsHint.Should().BeEmpty();
        model.ResourcesHint.Should().BeEmpty();
        model.LoadHint.Should().BeEmpty();
    }

    [Fact]
    public void Build_ActiveBurst_LeavesOnlyBurstCancelFlagEnabled()
    {
        var state = OperatorConsoleState.Empty with
        {
            ActiveMutation = new ActiveMutation(MutationKind.PaymentBurst, "burst", DateTimeOffset.UnixEpoch),
            Burst = new BurstProgress(10, 3, 3, 0, 0, false),
            CanResendLastPayment = true,
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.IsBusy.Should().BeTrue();
        model.CanCancelBurst.Should().BeTrue();
        model.CanResend.Should().BeFalse();
        model.BurstStatus.Should().Contain("3/10");
    }

    // --- Outcome feedback loop projections -------------------------------------------------

    private static readonly DateTimeOffset ProcessedAt = new(2026, 9, 5, 12, 4, 31, 882, TimeSpan.Zero);

    private static OperatorConsoleState Listening(params TrackedPayment[] payments) =>
        OperatorConsoleState.Empty with
        {
            Profile = TopologyProfile.Regular,
            Ownership = TopologyOwnership.Owned,
            Topology = OperatorHarness.Snapshot(TopologyProfile.Regular),
            ResourceAuthorityAvailable = true,
            Feed = new OutcomeFeedStatus(
                OutcomeFeedState.Listening,
                ListeningSince: new DateTimeOffset(2026, 9, 5, 12, 1, 4, TimeSpan.Zero)),
            TrackedPayments = payments,
        };

    private static TrackedPayment Submitted(
        string transactionId = "tx-8821",
        PaymentTrackingState state = PaymentTrackingState.Awaiting,
        PaymentOutcome httpOutcome = PaymentOutcome.Pending,
        int statusCode = 202) =>
        new(
            1,
            transactionId,
            PaymentRail.Standard,
            250m,
            "EUR",
            "1001",
            "2002",
            new DateTimeOffset(2026, 9, 5, 11, 59, 46, TimeSpan.Zero),
            httpOutcome,
            statusCode,
            state);

    [Fact]
    public void Build_AwaitingRow_StatesTheFeedInlineAndTheElapsedTime()
    {
        var model = PresentationModelBuilder.Build(Listening(Submitted()), Now);

        var row = model.Payments.Single();
        row.Symbol.Should().Be("~");
        row.Headline.Should().Be("Awaiting settlement — tx-8821");
        row.Meta.Should().Contain("14s").And.Contain("(listening)");
        model.FeedStatus.Should().Be("Listening since 12:01:04 — events before this time were not observed");
    }

    [Fact]
    public void Build_AwaitingRowUnderFaults_NamesTheConditionRatherThanImplyingADefect()
    {
        var state = Listening(Submitted()) with
        {
            FaultsArmed = true,
            AppliedFaults = FaultLevels.AllZero with { ErrorRatePercent = 40 },
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.Payments.Single().Meta.Should().Contain("faults in force");
    }

    [Fact]
    public void Build_SettledRow_PrintsBothClocksSeparatelyAndTheAlignedLegs()
    {
        var settled = Submitted(state: PaymentTrackingState.Settled) with
        {
            BroadcastOutcome = PaymentOutcome.Completed,
            ProcessedAt = ProcessedAt,
            ObservedAt = ProcessedAt.AddMilliseconds(222),
            Legs =
            [
                new SettlementLeg("1001", -250m, 4750m, "EUR", ProcessedAt),
                new SettlementLeg("2002", 250m, 1180m, "EUR", ProcessedAt),
            ],
        };

        var row = PresentationModelBuilder.Build(Listening(settled), Now).Payments.Single();

        row.Symbol.Should().Be("●");
        row.Headline.Should().Be("Settled — tx-8821");
        row.Meta.Should().Contain("ProcessedAt 12:04:31.882").And.Contain("observed here +222 ms");
        row.Legs.Should().Equal("1001  −250.00 → 4,750.00 EUR", "2002  +250.00 → 1,180.00 EUR");
        row.LegSummary.Should().BeEmpty();
    }

    [Fact]
    public void Build_HalfSettledRow_SaysSoRatherThanPaperingOverTheGap()
    {
        var halfSettled = Submitted(state: PaymentTrackingState.Settled) with
        {
            BroadcastOutcome = PaymentOutcome.Completed,
            ProcessedAt = ProcessedAt,
            ObservedAt = ProcessedAt,
            Legs = [new SettlementLeg("1001", -250m, 4750m, "EUR", ProcessedAt)],
        };

        PresentationModelBuilder.Build(Listening(halfSettled), Now)
            .Payments.Single().LegSummary.Should().Be("1 of 2 legs observed");
    }

    [Fact]
    public void Build_RejectedRow_CarriesTheFullErrorReasonAndExplainsTheEmptyLegColumn()
    {
        var rejected = Submitted(state: PaymentTrackingState.Rejected) with
        {
            BroadcastOutcome = PaymentOutcome.Failed,
            ProcessedAt = ProcessedAt,
            ObservedAt = ProcessedAt,
            ErrorReason = "insufficient funds",
        };

        var row = PresentationModelBuilder.Build(Listening(rejected), Now).Payments.Single();

        row.Symbol.Should().Be("✕");
        row.Headline.Should().Be("Rejected — tx-8821");
        row.Meta.Should().Contain("ErrorReason: insufficient funds");
        row.Legs.Should().BeEmpty();
        row.LegSummary.Should().Contain("a rejection emits none");
    }

    [Fact]
    public void Build_ContradictionRow_ShowsBothSourcesAndOffersTheOutcomeQuery()
    {
        var contradicted = Submitted(state: PaymentTrackingState.Contradiction, httpOutcome: PaymentOutcome.Completed, statusCode: 200) with
        {
            BroadcastOutcome = PaymentOutcome.Failed,
            ProcessedAt = ProcessedAt,
            ObservedAt = ProcessedAt,
            Note = "HTTP proved Completed, broadcast says Failed",
        };

        var row = PresentationModelBuilder.Build(Listening(contradicted), Now).Payments.Single();

        row.Headline.Should().Contain("Contradiction — HTTP proved Completed, broadcast says Failed");
        row.Meta.Should().Contain("HTTP said Completed").And.Contain("broadcast said Failed");
        row.Remedy.Should().Contain("Query outcome");
    }

    [Fact]
    public void Build_FeedLost_WithdrawsTheAwaitingWordingAndHeadlinesTheCount()
    {
        var unknown = Submitted(state: PaymentTrackingState.OutcomeUnknown) with
        {
            Note = "the console stopped listening at 12:06:02",
        };
        var state = Listening(unknown) with
        {
            Feed = new OutcomeFeedStatus(
                OutcomeFeedState.Lost,
                LostAt: new DateTimeOffset(2026, 9, 5, 12, 6, 2, TimeSpan.Zero)),
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.Payments.Single().Symbol.Should().Be("○");
        model.Payments.Single().Headline.Should()
            .Contain("Outcome unknown").And.Contain("stopped listening at 12:06:02");
        model.Payments.Single().Headline.Should().NotContain("Awaiting settlement");
        model.FeedStatus.Should().Contain("Feed lost 12:06:02")
            .And.Contain("1 payment has unknown outcomes", "the header and the evidence record share one formatter");
    }

    [Fact]
    public void Build_FeedNeverEstablished_NamesTheOutcomeQueryAsTheWayForward()
    {
        var notObserved = Submitted(state: PaymentTrackingState.NotObserved) with
        {
            Note = "daprd is not on PATH",
        };
        var state = Listening(notObserved) with
        {
            Feed = new OutcomeFeedStatus(OutcomeFeedState.Unavailable, Detail: "daprd is not on PATH"),
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.Payments.Single().Headline.Should().Contain("Outcome not observed — no feed");
        model.Payments.Single().Remedy.Should().Contain("Query outcome");
        model.FeedStatus.Should().Contain("Outcome not observed — no feed").And.Contain("daprd is not on PATH");
    }

    [Fact]
    public void Build_FeedResumed_StampsTheUnobservedWindow()
    {
        var state = Listening() with
        {
            Feed = new OutcomeFeedStatus(
                OutcomeFeedState.Listening,
                ListeningSince: new DateTimeOffset(2026, 9, 5, 12, 6, 19, TimeSpan.Zero),
                GapStart: new DateTimeOffset(2026, 9, 5, 12, 6, 2, TimeSpan.Zero),
                GapEnd: new DateTimeOffset(2026, 9, 5, 12, 6, 19, TimeSpan.Zero)),
        };

        PresentationModelBuilder.Build(state, Now).FeedStatus
            .Should().Be("Listening again — no events observed 12:06:02–12:06:19");
    }

    [Fact]
    public void Build_Burst_RendersTheHttpLegAndTheProvenLegAsTwoLabelledLines()
    {
        var state = Listening() with
        {
            Burst = new BurstProgress(10, 10, 10, 0, 0, false, Settled: 4, Rejected: 1),
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.BurstStatus.Should().StartWith("HTTP leg").And.Contain("10/10").And.Contain("accepted 10");
        model.BurstProvenStatus.Should().Be("Proven leg · settled 4 · rejected 1 · awaiting 5");
    }

    [Fact]
    public void Build_InboundEventRow_CarriesTheGutterMarkerRatherThanAColour()
    {
        var record = new EvidenceRecord(
            9,
            ProcessedAt,
            TopologyProfile.Regular,
            1,
            EvidenceKind.OutcomeEvent,
            "Settled — tx-8821",
            "com.corebank.transaction.completed",
            "tx-8821",
            null,
            TimeSpan.Zero,
            "detail",
            true,
            TransactionId: "tx-8821");
        var state = Listening() with { Evidence = [record] };

        // The inbound marker sits left of the status gutter rather than replacing it, so a
        // failed inbound event still reads as failed.
        PresentationModelBuilder.Build(state, Now).Evidence.Single().Summary
            .Should().Be("< ● Settled — tx-8821");
    }

    [Fact]
    public void Build_ColdOperations_ShowsNoFeedRatherThanAnEmptyPromise()
    {
        var model = PresentationModelBuilder.Build(OperatorConsoleState.Empty, Now);

        model.Payments.Should().BeEmpty();
        model.FeedStatus.Should().Contain("No outcome feed");
        model.BurstProvenStatus.Should().Contain("awaiting 0");
    }
}
