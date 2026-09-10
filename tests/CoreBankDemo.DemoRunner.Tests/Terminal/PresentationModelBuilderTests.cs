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

    /// <summary>Everything the Details pane puts on screen, for assertions that do not care which pane.</summary>
    private static string PaneText(OperatorPresentationModel model) => string.Join(
        Environment.NewLine,
        model.SelectedEvidencePane.Header,
        model.SelectedEvidencePane.Left,
        model.SelectedEvidencePane.Right ?? string.Empty);

    [Fact]
    public void Build_EmptyState_ShowsFiveWorkspacesAndColdPlaceholders()
    {
        var model = PresentationModelBuilder.Build(OperatorConsoleState.Empty, Now);

        model.Navigation.Should().HaveCount(5);
        model.Navigation.Should().Contain(item => item.Shortcut == "1" && item.Label == "Operations");
        model.Navigation.Should().Contain(item => item.Shortcut == "5" && item.Label == "Faults");
        model.FocusCard.IsPlaceholder.Should().BeTrue();
        model.FocusCard.StateWord.Should().Be("No payment yet this session.");
        model.FocusCard.ActionLabel.Should().Be(CardActions.Cancel, "the slot is never empty");
        model.FocusCard.ActionEnabled.Should().BeFalse();
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
    public void FormatBody_JsonPayload_IsIndentedForReading()
    {
        var formatted = PresentationModelBuilder.FormatBody(
            "{\"transactionId\":\"tx-8821\",\"status\":\"Completed\",\"legs\":[{\"account\":\"1001\"}]}");

        formatted.Should().Contain(Environment.NewLine, "a one-line payload hides its own fields on stage");
        formatted.Should().Contain("  \"transactionId\": \"tx-8821\"");
        formatted.Should().Contain("\"legs\": [");
    }

    [Fact]
    public void FormatBody_JsonAfterAPrefixLine_IsStillIndented()
    {
        // The real shape of a payment record: an idempotency line, then the response body.
        // Testing only a bare-JSON detail is what let this ship unformatted the first time.
        var formatted = PresentationModelBuilder.FormatBody(
            "Idempotency Generated: generated-key" + Environment.NewLine
            + "{\"paymentId\":\"pay-1\",\"status\":\"Pending\"}");

        formatted.Should().StartWith("Idempotency Generated: generated-key", "the prefix is evidence too");
        formatted.Should().Contain("  \"paymentId\": \"pay-1\"");
    }

    [Fact]
    public void FormatBody_TextAfterTheJson_IsKeptRatherThanDropped()
    {
        var formatted = PresentationModelBuilder.FormatBody(
            "{\"status\":\"Pending\"}" + Environment.NewLine + "(truncated by the server)");

        formatted.Should().Contain("  \"status\": \"Pending\"")
            .And.EndWith("(truncated by the server)");
    }

    [Theory]
    [InlineData("aspire start exited with code 1")]
    [InlineData("{ this is not json after all")]
    [InlineData("")]
    public void FormatBody_NonJson_IsPassedThroughUntouched(string body) =>
        // A malformed or truncated body is still evidence; a parse failure is not licence to
        // hide what the service actually sent.
        PresentationModelBuilder.FormatBody(body).Should().Be(body);

    [Fact]
    public void EvidenceDetail_CarriesTheHeadersAndThePayload()
    {
        var record = new EvidenceRecord(
            11,
            DateTimeOffset.UnixEpoch,
            TopologyProfile.Regular,
            2,
            EvidenceKind.Payment,
            "Payment accepted 202",
            "POST",
            "/api/payments",
            202,
            TimeSpan.FromMilliseconds(45),
            "{\"transactionId\":\"tx-8821\"}",
            true,
            null,
            "tx-8821");
        var state = OperatorConsoleState.Empty with { Evidence = [record], SelectedEvidence = record };

        var model = PresentationModelBuilder.Build(state, Now);

        PaneText(model).Should().Contain("Payment accepted 202")
            .And.Contain("POST /api/payments")
            .And.Contain("HTTP 202")
            .And.Contain("Transaction: tx-8821")
            .And.Contain("  \"transactionId\": \"tx-8821\"");
        // The row and the pane were once two projections that drifted apart. There is now only
        // one: rows carry no detail of their own, so they cannot disagree with the pane by
        // construction rather than by assertion.
    }

    /// <summary>
    /// Evidence rows must stay cheap to project. Detail was once computed for every retained
    /// record on every render -- UTF-8 encoding, JSON parsing and re-serializing all 500 of them
    /// -- for text nothing read, which is half of why a post-burst console appeared to freeze.
    /// Re-adding a per-row detail field would silently restore that cost, so its absence is
    /// asserted rather than left to review.
    /// </summary>
    [Fact]
    public void EvidenceRow_CarriesNoPerRowDetail()
    {
        typeof(EvidenceRowViewModel).GetProperties()
            .Select(property => property.Name)
            .Should().NotContain(
                "Detail",
                "the Details pane reads SelectedEvidencePane for the one record being read");
    }

    [Fact]
    public void EvidenceDetail_EmptyBody_SaysSoRatherThanShowingABlankPane()
    {
        var record = new EvidenceRecord(
            12, DateTimeOffset.UnixEpoch, TopologyProfile.Regular, 1, EvidenceKind.Topology,
            "Topology attached", "attach", "Regular", null, TimeSpan.Zero, string.Empty, true);
        var state = OperatorConsoleState.Empty with { Evidence = [record], SelectedEvidence = record };

        // A Topology record is not an HTTP exchange at all, so the stated absence names that
        // rather than a response body it was never going to have.
        var pane = PresentationModelBuilder.Build(state, Now).SelectedEvidencePane;

        pane.Left.Should().Contain("was not an HTTP exchange");
        pane.Right.Should().BeNull("an empty column beside a full one reads as a broken console");
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
        model.SelectedEvidencePane.Left.Should().Contain("raw", "a load workflow keeps its investigation detail");
        model.SelectedEvidencePane.Right.Should().BeNull("a load workflow is an aggregate, not one exchange");
        model.LoadResults.Should().Contain(value => value.Contains("Inline instant settlement"));
        model.CanStopOrSwitch.Should().BeTrue();
        model.CanUseLoadTest.Should().BeTrue();
    }

    [Fact]
    public void Build_ColdState_ExplainsWhyEveryGatedControlIsUnavailable()
    {
        var model = PresentationModelBuilder.Build(OperatorConsoleState.Empty, Now);

        // Operations has no workspace hint row any more, so the reason and its one-step remedy
        // live on the resting card's own disabled action instead.
        model.FocusCard.IsPlaceholder.Should().BeTrue();
        model.FocusCard.ActionReason.Should().Contain("No topology attached");
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

        model.FocusCard.ActionReason.Should().Be(
            "Fill the lines above and press Enter.",
            "with a ready topology the resting card carries its ordinary invitation, not a reason");
        model.ResourcesHint.Should().BeEmpty();
        model.LoadHint.Should().BeEmpty();
    }

    [Fact]
    public void Build_ShapeMismatchedGraph_KeepsRowsCommandableAndPrescribesNoTopologyRestart()
    {
        var state = MismatchedRegularState();

        var model = PresentationModelBuilder.Build(state, Now);

        var row = model.Resources.Single(resource => resource.Name == KnownResources.CoreBankApi);
        row.NextAction.Should().Be("Start");
        row.CanMutate.Should().BeTrue("the operator's own stop leaves an expected graph, not a corrupt one");
        model.ResourcesHint.Should().NotContain("Stop and Start it again");
        model.ResourcesHint.Length.Should().BeLessThanOrEqualTo(78, "the hint has to render at 80 columns");
    }

    [Fact]
    public void Build_UnreadableSnapshot_RefusesEveryRowAndNamesRefresh()
    {
        var state = OperatorConsoleState.Empty with
        {
            Profile = TopologyProfile.Regular,
            Ownership = TopologyOwnership.Attached,
            Topology = OperatorHarness.UnreadableSnapshot(TopologyProfile.Regular),
            ResourceAuthorityAvailable = true,
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.Resources.Should().OnlyContain(row => !row.CanMutate && !row.CanRestart);
        model.ResourcesHint.Should().Contain("Refresh state");
    }

    [Fact]
    public void Build_ColdState_NoLongerNamesTheRemovedSwitchControl()
    {
        var state = OperatorConsoleState.Empty with
        {
            Preflight = FakePreflightRunner.ReadyReport(),
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.ResourcesHint.Should().NotContain("Switch");
    }

    /// <summary>
    /// <c>Unavailable</c> is the sentinel a row wears when nothing applies. It must never be
    /// dispatched and never be printed as a verb beside a resource name.
    /// </summary>
    [Fact]
    public void Build_StartingResource_OffersTheUnavailableSentinelAndNoCommand()
    {
        var state = MismatchedRegularState(ResourceCondition.Starting);

        var model = PresentationModelBuilder.Build(state, Now);

        var row = model.Resources.Single(resource => resource.Name == KnownResources.CoreBankApi);
        row.NextAction.Should().Be("Unavailable");
        Enum.TryParse<ResourceCommand>(row.NextAction, out _).Should().BeFalse();
    }

    private static OperatorConsoleState MismatchedRegularState(
        ResourceCondition coreBankCondition = ResourceCondition.Stopped) =>
        OperatorConsoleState.Empty with
        {
            Profile = TopologyProfile.Regular,
            Ownership = TopologyOwnership.Attached,
            ResourceAuthorityAvailable = true,
            Topology = OperatorHarness.Snapshot(
                TopologyProfile.Regular,
                Now,
                fingerprint: false,
                resources:
                [
                    new ResourceSnapshot(
                        KnownResources.CoreBankApi,
                        coreBankCondition,
                        coreBankCondition.ToString(),
                        [],
                        0,
                        InstanceNames: ["corebank-api-1", "corebank-api-2"],
                        AllowedCommands: Enum.GetValues<ResourceCommand>().ToHashSet()),
                    .. OperatorHarness.DefaultResources(TopologyProfile.Regular)
                        .Where(resource => resource.Name != KnownResources.CoreBankApi),
                ]),
        };

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
        model.BurstStatus.Should().Contain("3 / 10");
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
    public void Build_AwaitingCard_StatesTheRegionsFeedOnceAndTheElapsedTime()
    {
        var model = PresentationModelBuilder.Build(Listening(Submitted()), Now);

        model.FocusCard.Symbol.Should().Be("~");
        model.FocusCard.StateWord.Should().Be("AWAITING SETTLEMENT");
        model.FocusCard.Clock.Should().Be("14s");
        model.FocusCard.Accounts.Should().Be("1001 → 2002", "the card prints both accounts in full");
        model.FocusCard.Meta.Should().Contain("tx-8821").And.Contain("submitted 11:59:46");
        model.FocusCard.ActionLabel.Should().Be(CardActions.Cancel);
        model.FocusCard.ActionEnabled.Should().BeTrue();
        model.ShowStillOpen.Should().BeFalse("with one payment open the card is already showing it");
        model.StillOpen.Should().HaveCount(1);
        model.FeedStatus.Should().Be("Listening since 12:01:04 — events before this time were not observed");
    }

    /// <summary>
    /// Under injected faults a long wait is the <i>expected</i> result, so the card names the
    /// condition rather than letting the audience read the delay as a defect. Ported from the
    /// payment-row projection this layout replaced.
    /// </summary>
    [Fact]
    public void Build_AwaitingCardUnderFaults_NamesTheConditionRatherThanImplyingADefect()
    {
        var quiet = PresentationModelBuilder.Build(Listening(Submitted()), Now).FocusCard;

        var state = Listening(Submitted()) with
        {
            FaultsArmed = true,
            AppliedFaults = FaultLevels.AllZero with { ErrorRatePercent = 40 },
        };

        var card = PresentationModelBuilder.Build(state, Now).FocusCard;

        card.Closing.Should().ContainSingle().Which.Should().Contain("faults in force");
        card.StateWord.Should().Be("AWAITING SETTLEMENT", "the condition is a qualifier, never a state");
        quiet.Closing.Should().ContainSingle().Which.Should().NotContain(
            "faults",
            "a quiet session's card is not padded with a condition that is not in force");
    }

    [Fact]
    public void Build_SecondOpenPayment_RendersTheStripWithTruncatedIdentifiers()
    {
        var second = Submitted("tx-8822") with { Sequence = 2, Rail = PaymentRail.Instant };
        var state = Listening(Submitted(), second) with { SelectedPayment = "tx-8822" };

        var model = PresentationModelBuilder.Build(state, Now);

        model.ShowStillOpen.Should().BeTrue();
        model.StillOpen.Should().HaveCount(2);
        model.FocusCard.TransactionId.Should().Be("tx-8822", "selection drives the card and nothing else does");
        model.StillOpen.Single(row => row.TransactionId == "tx-8822").Selected.Should().BeTrue();
        model.StillOpen[0].Line.Should().Contain("Awaiting").And.Contain("250.00").And.Contain("standard");
    }

    [Fact]
    public void Build_SettledCard_PrintsTheAlignedLegsAsItsClosingBlock()
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

        var card = PresentationModelBuilder.Build(Listening(settled), Now).FocusCard;

        card.Symbol.Should().Be("●");
        card.StateWord.Should().Be("SETTLED");
        // Two clocks, never one: the bank's own ProcessedAt and the console's delivery delta as
        // two separate figures, above the legs that prove the money moved.
        card.Closing.Should().Equal(
            "ProcessedAt 12:04:31.882, observed here +222 ms",
            "1001  −250.00 → 4,750.00 EUR",
            "2002  +250.00 → 1,180.00 EUR");
        card.ActionLabel.Should().Be(CardActions.LookUpOutcome, "the payment is proven; there is nothing to cancel");
    }

    [Fact]
    public void Build_HalfSettledCard_SaysSoRatherThanPaperingOverTheGap()
    {
        var halfSettled = Submitted(state: PaymentTrackingState.Settled) with
        {
            BroadcastOutcome = PaymentOutcome.Completed,
            ProcessedAt = ProcessedAt,
            ObservedAt = ProcessedAt,
            Legs = [new SettlementLeg("1001", -250m, 4750m, "EUR", ProcessedAt)],
        };

        PresentationModelBuilder.Build(Listening(halfSettled), Now)
            .FocusCard.Closing.Should().Contain("1 of 2 legs observed");
    }

    [Fact]
    public void Build_RejectedCard_CarriesTheFullErrorReason()
    {
        var rejected = Submitted(state: PaymentTrackingState.Rejected) with
        {
            BroadcastOutcome = PaymentOutcome.Failed,
            ProcessedAt = ProcessedAt,
            ObservedAt = ProcessedAt,
            ErrorReason = "insufficient funds",
        };

        var card = PresentationModelBuilder.Build(Listening(rejected), Now).FocusCard;

        card.Symbol.Should().Be("✕");
        card.StateWord.Should().Be("REJECTED");
        card.Closing.Should().Contain("ErrorReason: insufficient funds");
    }

    /// <summary>
    /// The one place the console says <i>who</i> withdrew a payment rather than only that it was
    /// withdrawn, and the reason the state column is sized to the longer of the two words.
    /// </summary>
    [Fact]
    public void Build_RailCancelledCard_NamesTheRailRatherThanOnlyTheWithdrawal()
    {
        var cancelled = Submitted(
            state: PaymentTrackingState.Cancelled,
            httpOutcome: PaymentOutcome.Cancelled,
            statusCode: 504) with
        {
            Rail = PaymentRail.Instant,
        };

        var card = PresentationModelBuilder.Build(Listening(cancelled), Now).FocusCard;

        card.Symbol.Should().Be("⊘");
        card.StateWord.Should().Be("CANCELLED BY THE RAIL");
        card.Closing.Should().Contain("no money moved · safe to retry with a new key");
    }

    [Fact]
    public void Build_OperatorCancelledCard_StatesItsOwnSafeNextStep()
    {
        var cancelled = Submitted(state: PaymentTrackingState.Cancelled, httpOutcome: PaymentOutcome.Pending);

        var card = PresentationModelBuilder.Build(Listening(cancelled), Now).FocusCard;

        card.Symbol.Should().Be("⊘");
        card.StateWord.Should().Be("CANCELLED");
        card.Closing.Should().Equal(
            "withdrawn before execution · no money moved",
            "safe to retry with a new key");
    }

    [Fact]
    public void Build_Burst_CountsCancelledPaymentsOnItsOwnFigure()
    {
        var state = Listening() with
        {
            Burst = new BurstProgress(10, 10, 6, 3, 0, false, Settled: 2, Rejected: 0, CancelledPayments: 1),
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.BurstStatus.Should().Contain("Sent  10 / 10").And.Contain("failed 0");
        model.BurstProvenStatus.Should().Be(
            "Settled  2   rejected 0 · cancelled 1 · still moving 7 · unknown 0",
            "a cancelled payment is never counted as a rejection");
    }

    [Fact]
    public void Build_ContradictionCard_ShowsBothRecordsAndPicksNoWinner()
    {
        var contradicted = Submitted(state: PaymentTrackingState.Contradiction, httpOutcome: PaymentOutcome.Completed, statusCode: 200) with
        {
            BroadcastOutcome = PaymentOutcome.Failed,
            ProcessedAt = ProcessedAt,
            ObservedAt = ProcessedAt,
            Note = "HTTP proved Completed, broadcast says Failed",
        };

        var card = PresentationModelBuilder.Build(Listening(contradicted), Now).FocusCard;

        card.StateWord.Should().Be("CONTRADICTED");
        card.Closing.Should().HaveCount(2, "a contradiction *is* two records");
        card.Closing[0].Should().Contain("HTTP Completed");
        card.Closing[1].Should().Contain("broadcast Failed");
    }

    [Fact]
    public void Build_FeedLost_WithdrawsTheAwaitingWordingAndAnnouncesItAsANotice()
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

        model.FocusCard.Symbol.Should().Be("○");
        model.FocusCard.StateWord.Should().Be("OUTCOME UNKNOWN");
        model.FocusCard.StateWord.Should().NotContain("AWAITING");
        model.FeedStatus.Should().Contain("Feed lost 12:06:02")
            .And.Contain("1 payment has unknown outcomes", "the header and the evidence record share one formatter");
        model.Announcement.Should().Contain("Feed lost 12:06:02");
        model.AnnouncementIsFailure.Should().BeFalse("a lost feed proves nothing about any payment");
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

        model.FocusCard.StateWord.Should().Be("OUTCOME NOT OBSERVED");
        model.FocusCard.Closing.Should().Contain(line => line.Contains("Look up outcome"));
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
    public void Build_Burst_RendersSentAndSettledAsTwoLabelledLinesThatNeverMerge()
    {
        var state = Listening() with
        {
            Burst = new BurstProgress(10, 10, 10, 0, 0, false, Settled: 4, Rejected: 1),
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.BurstCaption.Should().Contain("BURST · 10 payments");
        model.BurstStatus.Should().StartWith("Sent").And.Contain("10 / 10").And.Contain("accepted 10");
        model.BurstProvenStatus.Should().Be("Settled  4   rejected 1 · cancelled 0 · still moving 5 · unknown 0");
        model.BurstClosing.Should().BeEmpty("five payments are still moving; nothing may claim the burst proved itself");
    }

    /// <summary>
    /// A count of payments nobody is listening for is not a wait. The moment the feed drops the
    /// Settled line itself changes rather than the screen gaining a badge, so the takeover is
    /// structurally incapable of displaying a wait nobody is performing (EXPERIENCE.md,
    /// Component Patterns, "Burst takeover").
    /// </summary>
    [Fact]
    public void Build_RunningBurstWhoseFeedDropped_RestatesStillMovingAsUnknown()
    {
        var burst = new BurstProgress(10, 10, 10, 0, 0, false, Settled: 4, Rejected: 1);
        burst.Awaiting.Should().Be(5, "the five the console is still listening for");

        var lost = burst with { Unknown = burst.Unknown + burst.Outstanding };
        var model = PresentationModelBuilder.Build(Listening() with { Burst = lost }, Now);

        model.BurstProvenStatus.Should()
            .Be("Settled  4   rejected 1 · cancelled 0 · still moving 0 · unknown 5");
        model.BurstClosing.Should()
            .Be(
                "5 payments have unknown outcomes — see Evidence",
                "the closing line states the shortfall rather than claiming the burst proved itself");
    }

    [Fact]
    public void Build_DrainedBurst_OnlyClaimsItProvedItselfWhenEveryFigureIsZero()
    {
        var drained = Listening() with { Burst = new BurstProgress(10, 10, 10, 0, 0, false, Settled: 10) };
        var shortfall = Listening() with { Burst = new BurstProgress(10, 10, 8, 0, 2, false, Settled: 8) };
        var one = Listening() with { Burst = new BurstProgress(10, 10, 9, 0, 1, false, Settled: 9) };

        PresentationModelBuilder.Build(drained, Now).BurstClosing
            .Should().Be("every payment proved itself · nothing left awaiting");
        PresentationModelBuilder.Build(shortfall, Now).BurstClosing
            .Should().Be("2 payments have unknown outcomes — see Evidence");
        PresentationModelBuilder.Build(one, Now).BurstClosing
            .Should().Be("1 payment has unknown outcomes — see Evidence");
    }

    /// <summary>
    /// A burst the operator stopped is captioned distinctly and never claims it proved itself:
    /// the payments it never sent are payments the operator asked for and got no outcome for,
    /// and the longest-lived sentence this console renders must not say otherwise.
    /// </summary>
    [Fact]
    public void Build_StoppedBurst_NamesTheUnsentRemainderRatherThanClaimingItProvedItself()
    {
        var stopped = Listening() with
        {
            Burst = new BurstProgress(200, 120, 120, 0, 0, Cancelled: true, Settled: 120),
        };

        var model = PresentationModelBuilder.Build(stopped, Now);

        model.BurstCaption.Should().Be("BURST · stopped · 120 of 200 sent");
        model.BurstCaption.Should().NotContain("drained");
        model.BurstStatus.Should().Contain("120 / 200", "the denominator never re-bases");
        model.BurstClosing.Should().Be(
            "80 payments have never been sent — see Evidence",
            "a stopped burst is not a proven burst");
    }

    /// <summary>
    /// Cancel is exempt from confirmation, never from the lock: it dims like every other mutating
    /// control while some other action is in flight, and the reason is stated on the card.
    /// </summary>
    [Fact]
    public void Build_AnotherMutationInFlight_DimsCancelAndSaysWhy()
    {
        var state = Listening(Submitted()) with
        {
            ActiveMutation = new ActiveMutation(MutationKind.ResourceCommand, "Stop corebank-api", Now),
        };

        var card = PresentationModelBuilder.Build(state, Now).FocusCard;

        card.ActionLabel.Should().Be(CardActions.Cancel, "the slot is never empty and never hidden");
        card.ActionEnabled.Should().BeFalse();
        card.ActionReason.Should().Contain("another action is in flight");
    }

    /// <summary>
    /// The one narrow concession: an outstanding answer is that payment's own state rather than a
    /// console action awaiting a result, which is what makes the budget window cancellable.
    /// </summary>
    [Fact]
    public void Build_ItsOwnSubmissionInFlight_KeepsCancelLive()
    {
        var inFlight = Submitted() with { AwaitingResponse = true, HttpStatusCode = 0 };
        var state = Listening(inFlight) with
        {
            ActiveMutation = new ActiveMutation(MutationKind.SubmitPayment, "Submit payment", Now),
        };

        var card = PresentationModelBuilder.Build(state, Now).FocusCard;

        card.ActionLabel.Should().Be(CardActions.Cancel);
        card.ActionEnabled.Should().BeTrue();
        card.Closing.Should().Contain("waiting for the bank to answer");
    }

    /// <summary>
    /// On dispatch the card's <i>action slot</i> — never its state — re-states itself, while the
    /// payment's own state and clock stay exactly as they were: asking is not an outcome.
    /// </summary>
    [Fact]
    public void Build_CancelInFlight_RestatesTheSlotAndLeavesTheStateAndClockAlone()
    {
        var state = Listening(Submitted()) with
        {
            CancellingPayment = "tx-8821",
            CancellingSince = Now.AddSeconds(-3),
        };

        var card = PresentationModelBuilder.Build(state, Now).FocusCard;

        card.ActionLabel.Should().Be("Cancelling — 3s");
        card.ActionEnabled.Should().BeFalse("a second press mid-sentence cannot become a second cancellation");
        card.StateWord.Should().Be("AWAITING SETTLEMENT");
        card.Clock.Should().Be("14s", "the payment's own clock is untouched by the ask");
    }

    /// <summary>
    /// Omitted mode sends no key, so the bank names the payment and there is no id to cancel
    /// with. The action still renders — disabled, with the reason on the card.
    /// </summary>
    [Fact]
    public void Build_OmittedSubmissionWithNoId_RendersADisabledCancelWithItsReason()
    {
        var state = Listening() with
        {
            UnidentifiedSubmission = new UnidentifiedSubmission(
                new PaymentRequest("1001", "2002", 250m, "EUR", PaymentRail.Instant),
                Now.AddSeconds(-3)),
        };

        var card = PresentationModelBuilder.Build(state, Now).FocusCard;

        card.StateWord.Should().Be("NO ANSWER YET");
        card.ActionLabel.Should().Be(CardActions.Cancel);
        card.ActionEnabled.Should().BeFalse();
        card.ActionReason.Should().Contain("no transaction id yet");
        card.TransactionId.Should().BeNull();
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
            "Settled · tx-8821",
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
            .Should().Be("< ● Settled · tx-8821", "the row keeps its identity; only a clause is droppable");
    }

    [Fact]
    public void Build_ColdOperations_ShowsNoFeedRatherThanAnEmptyPromise()
    {
        var model = PresentationModelBuilder.Build(OperatorConsoleState.Empty, Now);

        model.StillOpen.Should().BeEmpty();
        model.ShowStillOpen.Should().BeFalse();
        model.FeedStatus.Should().Contain("No outcome feed");
        model.BurstProvenStatus.Should().Contain("still moving 0");
    }

    // --- The Details pane, one row of the I/O matrix at a time -----------------------------

    [Fact]
    public void EvidencePane_Exchange_ReadsAsTwoRawHttpColumns()
    {
        var pane = PresentationModelBuilder.EvidencePane(PaymentRecord(Exchange()));

        // FR-14: each column reads top to bottom as a raw exchange -- start line, headers, blank
        // line, body -- under a title that costs no extra control.
        pane.Left.Should().StartWith("REQUEST" + Environment.NewLine + "POST http://127.0.0.1:5294/api/payments");
        pane.Left.Should().Contain("Idempotency-Key: demo-key-001")
            .And.Contain("Content-Type: application/json");
        pane.Left.Should().Contain("  \"amount\": 250", "a one-line payload hides its own fields on stage");
        pane.Right.Should().StartWith("RESPONSE" + Environment.NewLine + "HTTP 202 Accepted");
        pane.Right.Should().Contain("  \"transactionId\": \"tx-8821\"");
    }

    [Fact]
    public void EvidencePane_ExchangeThatGotNoAnswer_SaysSoAndKeepsTheConsolesOwnAccount()
    {
        var record = PaymentRecord(Exchange() with
        {
            StatusCode = null,
            ReasonPhrase = null,
            ResponseHeaders = [],
            ResponseBody = null,
        }) with
        {
            Detail = "Request timed out; the server may have accepted it.",
        };

        var pane = PresentationModelBuilder.EvidencePane(record);

        pane.Left.Should().Contain("Idempotency-Key: demo-key-001", "the request is still evidence");
        pane.Right.Should().Contain("no answer arrived")
            .And.Contain("Request timed out", "the only account of what went wrong is this console's own");
    }

    [Fact]
    public void EvidencePane_NonJsonBody_IsShownVerbatimRatherThanMangled()
    {
        var record = PaymentRecord(Exchange() with { ResponseBody = "<html>upstream timed out</html>" });

        PresentationModelBuilder.EvidencePane(record).Right
            .Should().Contain("<html>upstream timed out</html>");
    }

    [Fact]
    public void EvidencePane_EmptyResponseBody_SaysSoRatherThanShowingABlankColumn()
    {
        var record = PaymentRecord(Exchange() with { ResponseBody = string.Empty });

        PresentationModelBuilder.EvidencePane(record).Right.Should().Contain("(no response body was recorded)");
    }

    [Fact]
    public void EvidencePane_CloudEvent_IsOneFullWidthColumnAndNoEmptyRequestBeside()
    {
        var record = EventRecord();

        var pane = PresentationModelBuilder.EvidencePane(record);

        pane.Right.Should().BeNull("an EVENT record renders no empty REQUEST column beside it");
        pane.Left.Should().StartWith("EVENT");
        pane.Left.Should().Contain("Type: com.corebank.transaction.completed")
            .And.Contain("Id: evt-1")
            .And.Contain("Source: corebank")
            .And.Contain("SpecVersion: 1.0")
            .And.Contain("PubSubName: pubsub")
            .And.Contain("Topic: transaction-events")
            .And.Contain("route: /outcome");
        pane.Left.Should().Contain("  \"transactionId\": \"tx-8821\"");
        // Neither is exposed by the SDK, so neither is invented.
        pane.Left.Should().NotContain("subject").And.NotContain("time:");
    }

    [Theory]
    [InlineData(EvidenceKind.Topology)]
    [InlineData(EvidenceKind.Resource)]
    [InlineData(EvidenceKind.Export)]
    [InlineData(EvidenceKind.Fault)]
    [InlineData(EvidenceKind.Burst)]
    [InlineData(EvidenceKind.LoadTest)]
    public void EvidencePane_PayloadLessKind_StatesTheAbsenceAndRendersNoEmptyColumn(EvidenceKind kind)
    {
        var record = PaymentRecord(null) with { Kind = kind, Detail = "aspire start exited with code 1" };

        var pane = PresentationModelBuilder.EvidencePane(record);

        pane.Header.Should().Contain("202 Pending", "the header block is unchanged");
        pane.Left.Should().Contain("was not an HTTP exchange")
            .And.Contain("aspire start exited with code 1", "the console's own record of it is still evidence");
        pane.Right.Should().BeNull();
        pane.CopyText.Should().StartWith(pane.Header).And.Contain("aspire start exited with code 1");
    }

    [Fact]
    public void EvidencePane_PayloadLessRecordWithNoDetail_StillHasSomethingToCopy()
    {
        // A Fault record passes `ErrorSummary ?? string.Empty` as its detail, so an empty one is
        // ordinary. Copy reporting "there is nothing to copy" while the pane visibly shows a
        // header block would be the console contradicting itself on stage.
        var record = PaymentRecord(null) with { Kind = EvidenceKind.Fault, Detail = string.Empty };

        var pane = PresentationModelBuilder.EvidencePane(record);

        pane.Left.Should().Contain("was not an HTTP exchange");
        pane.CopyText.Should().NotBeNullOrWhiteSpace().And.Be(pane.Header);
    }

    [Fact]
    public void EvidencePane_Copy_IsTheHeaderBlockThenRawTextWithNoColumnArt()
    {
        var pane = PresentationModelBuilder.EvidencePane(PaymentRecord(Exchange()));

        // The header block is copied too: a record whose timestamp, profile, duration, faults
        // and transaction id could not be copied would be half a record.
        pane.CopyText.Should().StartWith(pane.Header)
            .And.Contain("Transaction: tx-8821");
        // FR-17: the rest has to paste into a .http file or Postman and run.
        pane.CopyText.Should().Contain(
            Environment.NewLine + Environment.NewLine + "POST http://127.0.0.1:5294/api/payments");
        pane.CopyText.Should().Contain("""{"amount":250,"currency":"EUR"}""", "the bytes as sent, not as re-indented");
        pane.CopyText.Should().Contain(
            Environment.NewLine + Environment.NewLine + "HTTP 202 Accepted",
            "raw request, blank line, raw response");
        pane.CopyText.Should().NotContain("REQUEST").And.NotContain("RESPONSE");
    }

    [Fact]
    public void EvidencePane_CopyOfACallThatGotNoAnswer_CarriesNoProse()
    {
        var record = PaymentRecord(Exchange() with
        {
            StatusCode = null,
            ReasonPhrase = null,
            ResponseHeaders = [],
            ResponseBody = null,
        }) with
        {
            Detail = "Request timed out; the server may have accepted it.",
        };

        var pane = PresentationModelBuilder.EvidencePane(record);

        pane.Right.Should().Contain("no answer arrived").And.Contain("Request timed out");
        pane.CopyText.Should().NotContain("no answer arrived")
            .And.NotContain("Request timed out", "prose is not raw response bytes");
        pane.CopyText.Should().Contain("POST http://127.0.0.1:5294/api/payments");
    }

    [Fact]
    public void EvidencePane_CopyOfACloudEvent_CarriesTheHeaderAndTheRawEnvelope()
    {
        var pane = PresentationModelBuilder.EvidencePane(EventRecord());

        pane.CopyText.Should().StartWith(pane.Header)
            .And.Contain("Type: com.corebank.transaction.completed")
            .And.Contain("""{"transactionId":"tx-8821","status":"Completed"}""")
            .And.NotContain("EVENT" + Environment.NewLine);
    }

    [Fact]
    public void EvidencePane_LongSummary_IsCutOnTheRowAndWholeEverywhereElse()
    {
        var record = PaymentRecord(null) with
        {
            Summary = "202 Pending — no committed outcome yet",
        };
        var state = OperatorConsoleState.Empty with { Evidence = [record], SelectedEvidence = record };

        var model = PresentationModelBuilder.Build(state, Now);

        model.Evidence.Single().Summary.Should().Be("  ● 202 Pending");
        model.SelectedEvidencePane.Header.Should().Contain("202 Pending — no committed outcome yet");
    }

    [Fact]
    public void RowSummary_WithoutAnEmDash_IsUnchanged() =>
        PresentationModelBuilder.RowSummary("Inspected payments.outbox").Should().Be("Inspected payments.outbox");

    [Theory]
    [InlineData("— nothing before the dash")]
    [InlineData("   — only blank before the dash")]
    public void RowSummary_WithNothingBeforeTheEmDash_KeepsTheWholeSummary(string summary) =>
        // A row cut down to a gutter marker and a status glyph identifies nothing at all, which
        // is worse than the long row truncation exists to shorten.
        PresentationModelBuilder.RowSummary(summary).Should().Be(summary);

    private static HttpExchange Exchange() => new(
        "POST",
        "http://127.0.0.1:5294/api/payments",
        [new EvidenceHeader("Idempotency-Key", "demo-key-001"), new EvidenceHeader("Content-Type", "application/json")],
        """{"amount":250,"currency":"EUR"}""",
        202,
        "Accepted",
        [new EvidenceHeader("Content-Type", "application/json")],
        """{"transactionId":"tx-8821","status":"Pending"}""");

    private static EvidenceRecord PaymentRecord(HttpExchange? exchange) => new(
        21,
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
        TransactionId: "tx-8821",
        Exchange: exchange);

    private static EvidenceRecord EventRecord() => new(
        22,
        DateTimeOffset.UnixEpoch,
        TopologyProfile.Regular,
        1,
        EvidenceKind.OutcomeEvent,
        "Settled · tx-8821",
        OutcomeEventTypes.TransactionCompleted,
        "tx-8821",
        null,
        TimeSpan.Zero,
        "detail",
        true,
        TransactionId: "tx-8821",
        Event: new CloudEventRecord(
            "evt-1",
            "corebank",
            OutcomeEventTypes.TransactionCompleted,
            "1.0",
            "application/json",
            OutcomeEventTypes.PubSubComponent,
            OutcomeEventTypes.Topic,
            "/outcome",
            [new EvidenceHeader("route", "/outcome")],
            """{"transactionId":"tx-8821","status":"Completed"}"""));

    /// <summary>
    /// The console must offer Start for the resource the operator just stopped. Aspire reports
    /// that resource as Completed (its "Finished" state), so a next action defined only for
    /// Stopped left the button reading "Unavailable" and the demo with no way to resume.
    /// </summary>
    [Fact]
    public void Build_CompletedResource_OffersStartRatherThanNoActionAtAll()
    {
        var stopped = new ResourceSnapshot(
            KnownResources.CoreBankApi,
            ResourceCondition.Completed,
            "Finished",
            [],
            ReplicaCount: 2,
            InstanceNames: ["corebank-api-a", "corebank-api-b"],
            AllowedCommands: new HashSet<ResourceCommand> { ResourceCommand.Start });

        var snapshot = OperatorHarness.Snapshot(TopologyProfile.Regular, fingerprint: false, resources: stopped);
        var state = OperatorConsoleState.Empty with
        {
            Profile = TopologyProfile.Regular,
            Ownership = TopologyOwnership.Owned,
            RunGeneration = 1,
            Topology = snapshot,
            ResourceAuthorityAvailable = true,
        };

        var model = PresentationModelBuilder.Build(state, Now);
        var row = model.Resources.Single(candidate => candidate.Name == KnownResources.CoreBankApi);

        row.NextAction.Should().Be("Start");
        row.CanMutate.Should().BeTrue();
    }
}
