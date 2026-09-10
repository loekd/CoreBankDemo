using System.Text;
using System.Text.Json;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Terminal;

public sealed record NavigationItemViewModel(WorkspaceKind Workspace, string Shortcut, string Label, bool Active);

public sealed record ResourceRowViewModel(
    string Name,
    IReadOnlyList<string> Instances,
    string Symbol,
    string State,
    string Detail,
    string NextAction,
    bool CanMutate,
    bool CanRestart);

/// <summary>
/// The knob captions, named once. The Faults workspace renders its rows straight from
/// <see cref="FaultsViewModel.Knobs"/>, so a knob added or reordered here cannot desync from
/// the labels drawn beside it.
/// </summary>
public static class FaultKnobs
{
    public const string ErrorRate = "Error rate";
    public const string LatencyBand = "Latency band";
    public const string Throttling = "Throttling";
}

/// <summary>
/// One fault knob, always carrying its exact number as text. The bar is reinforcement; the
/// number is the authoritative reading and survives a monochrome terminal and a projector.
/// </summary>
/// <param name="ValueText">Live level alone, or the explicit delta (<c>5% → 40%</c>) when staged.</param>
public sealed record FaultKnobViewModel(
    string Name,
    string LiveText,
    string StagedText,
    string ValueText,
    bool IsStaged);

/// <summary>
/// The Faults workspace's whole readable state. Severity lives in the numbers and the bar
/// length only — nothing here encodes it as a colour.
/// </summary>
public sealed record FaultsViewModel(
    bool Available,
    string DisabledReason,
    string ChipSymbol,
    string ChipLabel,
    IReadOnlyList<FaultKnobViewModel> Knobs,
    IReadOnlyList<FaultPreset> Presets,
    string PresetLabel,
    bool CanApply,
    string ApplyCaption,
    string Detail,
    string CostNote,
    FaultLevels Live,
    FaultLevels Staged);

/// <summary>
/// One row of the evidence list. Deliberately carries no expanded detail: the Details pane reads
/// the separately computed <see cref="OperatorPresentationModel.SelectedEvidencePane"/> for the
/// one record the operator actually selected. A per-row copy meant re-parsing and re-serializing
/// every retained payload on every render, for text nothing ever read.
/// </summary>
public sealed record EvidenceRowViewModel(
    long Sequence,
    string Summary,
    string Provenance,
    bool Succeeded);

/// <summary>
/// The Details pane for the one selected record: a header block, then the payload as columns.
/// <para>
/// <see cref="Left"/> and <see cref="Right"/> are what the two column panes show — the first
/// line of each is its own title, so two columns cost two controls rather than four. A record
/// that is not a two-sided HTTP exchange (an inbound CloudEvent, an Aspire CLI invocation, a
/// local file write) has a <see cref="Right"/> of <see langword="null"/> and its left column
/// runs full width: an empty column beside a full one reads as a broken console.
/// </para>
/// </summary>
/// <param name="CopyText">
/// What Copy puts on the clipboard: the raw request text, a blank line, the raw response text.
/// Never the column titles and never this console's re-indentation — it has to paste into a
/// <c>.http</c> file or Postman and run.
/// </param>
public sealed record EvidencePaneViewModel(
    string Header,
    string Left,
    string? Right,
    string CopyText)
{
    public static readonly EvidencePaneViewModel Empty = new(
        "No action selected. Select a row on the left, or press Details.",
        string.Empty,
        null,
        string.Empty);
}

/// <summary>
/// The Operations focus card: one large object rendering the <b>selected</b> payment, and the
/// only place in Operations a payment is read in full. Its first line is a fixed grid — symbol,
/// state word, clock, action — so nothing on it moves as the state resolves.
/// </summary>
/// <param name="StateWord">Never abbreviated: the lifecycle line gives up its room first, and the clock after it.</param>
/// <param name="Clock">The elapsed clock while unresolved, the final clock once proven.</param>
/// <param name="ActionLabel">Cancel payment / Look up outcome / Resend same key — one slot, never empty.</param>
/// <param name="ActionReason">Why the action is disabled, stated on the card rather than implied.</param>
/// <param name="Closing">
/// Exactly one closing block: the lifecycle in words while unresolved, the balance legs once
/// settled, or the ErrorReason once rejected — with the single named exception that a
/// contradiction holds two records, because a contradiction <i>is</i> two records.
/// </param>
public sealed record FocusCardViewModel(
    string Symbol,
    string StateWord,
    string Clock,
    string ActionLabel,
    bool ActionEnabled,
    string ActionReason,
    string RequestDetail,
    string Accounts,
    string Meta,
    IReadOnlyList<string> Closing,
    string? TransactionId,
    bool IsPlaceholder)
{
    /// <summary>The resting card, before the first submission this session. Never a blank region.</summary>
    public static FocusCardViewModel Placeholder { get; } = new(
        " ",
        "No payment yet this session.",
        string.Empty,
        CardActions.Cancel,
        false,
        "Fill the lines above and press Enter.",
        string.Empty,
        string.Empty,
        string.Empty,
        [],
        null,
        true);
}

/// <summary>The three labels the focus card's single action slot ever carries.</summary>
public static class CardActions
{
    public const string Cancel = "Cancel payment";
    public const string LookUpOutcome = "Look up outcome";
    public const string ResendSameKey = "Resend same key";
}

/// <summary>
/// One line of the STILL OPEN strip: a work queue, never a log. A payment enters when it is
/// submitted and leaves the moment its outcome is proven. Account identifiers truncate to their
/// last four digits here and only here — the card above always prints them in full.
/// </summary>
public sealed record StillOpenRowViewModel(
    string TransactionId,
    bool Selected,
    string Symbol,
    string Status,
    string Amount,
    string Accounts,
    string Rail,
    string Clock)
{
    /// <summary>The rendered line, selection marker included, so the strip reads identically everywhere.</summary>
    public string Line =>
        $"{(Selected ? "\u25b8" : " ")} {Symbol} {Status,-18} {Amount,10}  {Accounts}  {Rail,-8} {Clock,7}";
}

public sealed record OperatorPresentationModel(
    string TopologyBar,
    IReadOnlyList<NavigationItemViewModel> Navigation,
    WorkspaceKind ActiveWorkspace,
    IReadOnlyList<ResourceRowViewModel> Resources,
    IReadOnlyList<EvidenceRowViewModel> Evidence,
    EvidencePaneViewModel SelectedEvidencePane,
    FaultsViewModel Faults,
    FocusCardViewModel FocusCard,
    IReadOnlyList<StillOpenRowViewModel> StillOpen,
    bool ShowStillOpen,
    string Announcement,
    bool AnnouncementIsFailure,
    string FeedStatus,
    string BurstCaption,
    string BurstStatus,
    string BurstProvenStatus,
    string BurstClosing,
    string LoadPhaseStatus,
    IReadOnlyList<string> LoadResults,
    bool IsBusy,
    bool CanCancelBurst,
    bool CanStopOrSwitch,
    bool CanUseLoadTest,
    bool CanResend,
    string ResourcesHint,
    string LoadHint,
    string ArmingCaption,
    bool CanChangeArming,
    // What the console is doing or last did to the topology. Rendered in Resources, which is the
    // workspace that owns the question it answers -- the removed bottom band showed it in all
    // five, and Evidence/Results is where the durable record of it lives.
    string TopologyStatus);

public static class PresentationModelBuilder
{
    /// <summary>
    /// Projects the console state for rendering. <paramref name="now"/> is required rather
    /// than defaulted because it drives the "applied — not yet observed" readout: a caller
    /// that forgot to pass a clock would silently render a console that never reports how
    /// long it has been waiting for proof.
    /// </summary>
    public static OperatorPresentationModel Build(OperatorConsoleState state, DateTimeOffset now)
    {
        var resources = (state.Topology?.Resources ?? [])
            .Select(resource =>
            {
                var nextAction = NextAction(resource.Condition);
                var supported = Enum.TryParse<ResourceCommand>(nextAction, out var command) && resource.Supports(command);
                return new ResourceRowViewModel(
                    resource.Name,
                    resource.InstanceNames ?? [resource.Name],
                    SymbolFor(resource.Condition),
                    LabelFor(resource.Condition),
                    BuildResourceDetail(resource),
                    supported ? nextAction : "Unavailable",
                    supported && CanMutateResource(state, resource),
                    resource.Condition is ResourceCondition.Healthy or ResourceCondition.Running
                    && resource.Supports(ResourceCommand.Restart)
                    && CanMutateResource(state, resource));
            })
            .ToList();

        var evidence = state.Evidence
            .OrderByDescending(record => record.Sequence)
            .Select(record => new EvidenceRowViewModel(
                record.Sequence,
                // An inbound gutter marker, not a colour: a scan down the list tells "what the
                // bank said" from "what the operator did" without reading a word
                // (DESIGN.md, Event row). It occupies a column *left of* the status gutter
                // rather than replacing it -- swallowing the glyph made a failed inbound event
                // indistinguishable from a settled one.
                record.Kind == EvidenceKind.OutcomeEvent
                    ? $"< {StatusGlyph(record.Succeeded)} {RowSummary(record.Summary)}"
                    : $"  {StatusGlyph(record.Succeeded)} {RowSummary(record.Summary)}",
                $"{KnownTopologyProfiles.DisplayName(record.Profile)} · generation {record.RunGeneration} · {record.Timestamp:HH:mm:ss}{FaultProvenance(record)}",
                record.Succeeded))
            .ToList();

        // One projection, used by both the row and the Details pane. They were built separately
        // and had already drifted: the pane omitted the timestamp the row showed, so the same
        // record read differently depending on where you looked at it.
        var selected = state.SelectedEvidence is null
            ? EvidencePaneViewModel.Empty
            : EvidencePane(state.SelectedEvidence);

        var loadResults = new List<string>();
        if (state.LastLoadResult is { } load)
        {
            loadResults.AddRange(load.Invariants.Select(invariant =>
                $"{(invariant.Passed ? "● PASS" : "✕ FAIL")} {invariant.Name}: {invariant.Detail}"));
            loadResults.Add($"{(load.InlineSettlement.Observed ? "● PASS" : "○ UNKNOWN")} Inline instant settlement: {load.InlineSettlement.Detail}");
        }
        else
        {
            loadResults.AddRange(new[]
            {
                "○ Exactly-once processing — not yet observed",
                "○ Zero message loss — not yet observed",
                "○ Balance conservation — not yet observed",
                "○ Terminal-state completeness — not yet observed",
                "○ Per-key ordering — not yet observed",
                "○ Inline instant settlement — not yet observed",
            });
        }

        var open = state.TrackedPayments.Where(payment => payment.IsOpen).ToList();
        var card = BuildFocusCard(state, open, now);
        var stillOpen = open
            .Select(payment => BuildStillOpenRow(payment, card.TransactionId, now))
            .ToList();

        var resourceSummary = resources.Count == 0
            ? "resources ○ Unknown"
            : string.Join(" ", resources.Select(resource => $"{Abbreviate(resource.Name)} {resource.Symbol}"));
        var faults = BuildFaults(state, now);
        var announcement = Announcement(state);
        var profile = KnownTopologyProfiles.DisplayName(state.Profile);
        var topologyBar = $"{profile} · {state.Ownership} · generation {state.RunGeneration} · "
            + $"{faults.ChipSymbol} {faults.ChipLabel} · {resourceSummary}";

        return new OperatorPresentationModel(
            topologyBar,
            Enum.GetValues<WorkspaceKind>().Select((workspace, index) =>
                new NavigationItemViewModel(workspace, (index + 1).ToString(), NavigationLabel(workspace), state.ActiveWorkspace == workspace)).ToList(),
            state.ActiveWorkspace,
            resources,
            evidence,
            selected,
            faults,
            card,
            stillOpen,
            // The strip renders only while more than one payment is open: with exactly one the
            // card is already showing it, and a strip beneath would list it under itself.
            open.Count > 1,
            announcement.Text,
            announcement.IsFailure,
            FeedStatusLine(state),
            BurstCaptionLine(state.Burst),
            // The two lines are never merged: a burst is exactly where "acknowledged" and
            // "finished" diverge, and `still moving` draining to zero is the burst's visual
            // confirmation.
            BurstSentLine(state.Burst),
            BurstProvenLine(state.Burst),
            BurstClosingLine(state.Burst),
            $"{state.LoadProgress.Phase} · {state.LoadProgress.Elapsed.TotalSeconds:F0}s · {state.LoadProgress.Detail}",
            loadResults,
            state.ActiveMutation is not null,
            state.ActiveMutation?.Kind == MutationKind.PaymentBurst,
            state.Ownership == TopologyOwnership.Owned && state.ActiveMutation is null,
            state.Profile == TopologyProfile.LoadTests
                && state.Ownership != TopologyOwnership.None
                && state.ActiveMutation is null
                && state.ResourceAuthorityAvailable
                && state.Topology?.IsReady == true,
            state.CanResendLastPayment && state.ActiveMutation is null,
            ResourcesHint(state),
            LoadHint(state),
            ArmingCaption(state),
            state.Ownership == TopologyOwnership.None,
            state.ActiveMutation is null
                ? state.StatusLine
                : $"{state.ActiveMutation.Kind} · {state.ActiveMutation.Target} · Running");
    }

    /// <summary>
    /// The Evidence feed header, and the one place the feed's start time is stated. An empty
    /// feed is only meaningful with a start time attached, so it always carries one — and a
    /// reconnect stamps the window this console did not observe rather than back-filling it.
    /// </summary>
    private static string FeedStatusLine(OperatorConsoleState state) => state.Feed switch
    {
        { State: OutcomeFeedState.Listening, GapStart: not null } gap =>
            OutcomeFeedNarrative.ListeningAgain(gap.GapStart, gap.GapEnd),
        { State: OutcomeFeedState.Listening, GapEnd: not null } gap =>
            OutcomeFeedNarrative.ListeningAgain(gap.GapStart, gap.GapEnd),
        { State: OutcomeFeedState.Listening } listening =>
            OutcomeFeedNarrative.ListeningSince(listening.ListeningSince),
        { State: OutcomeFeedState.Lost } lost => OutcomeFeedNarrative.FeedLost(
            lost.LostAt,
            state.TrackedPayments.Count(payment => payment.State == PaymentTrackingState.OutcomeUnknown)),
        { State: OutcomeFeedState.Unavailable } unavailable =>
            OutcomeFeedNarrative.Unavailable(unavailable.Detail),
        _ => OutcomeFeedNarrative.NotStarted(),
    };

    /// <summary>
    /// Which payment the card holds: the operator's explicit selection, then — with nothing
    /// selected — the single open payment, then the most recently resolved one, so a settled
    /// result stays readable instead of being cleared by its own success. Selection drives the
    /// card and nothing else does; an arriving event never re-points it.
    /// </summary>
    private static FocusCardViewModel BuildFocusCard(
        OperatorConsoleState state,
        IReadOnlyList<TrackedPayment> open,
        DateTimeOffset now)
    {
        var selected = state.SelectedPayment is { Length: > 0 } id
            ? state.TrackedPayments.FirstOrDefault(payment =>
                string.Equals(payment.TransactionId, id, StringComparison.Ordinal))
            : null;
        selected ??= open.Count == 1 ? open[0] : null;
        selected ??= state.TrackedPayments.LastOrDefault(payment => !payment.IsOpen);

        if (selected is null)
        {
            // An Omitted-mode submission in flight has no id to be tracked by, so it never
            // becomes a row -- but it is still a payment the console sent, and the card holds it
            // rather than leaving it unrepresented.
            return state.UnidentifiedSubmission is { } pending
                ? UnidentifiedCard(pending, now)
                : PlaceholderCard(state);
        }

        var (symbol, stateWord, closing) = CardState(state, selected);
        var (label, enabled, reason) = CardAction(state, selected, now);
        return new FocusCardViewModel(
            symbol,
            stateWord,
            CardClock(selected, now),
            label,
            enabled,
            reason,
            $"{selected.Rail.ToString().ToLowerInvariant()} · {selected.Amount:N2} {selected.Currency}",
            // The card prints both accounts in full; the strip's truncation never applies here.
            $"{selected.FromAccount} → {selected.ToAccount}",
            $"{selected.TransactionId} · submitted {OutcomeFeedNarrative.Clock(selected.SubmittedAt)}",
            closing,
            selected.TransactionId,
            false);
    }

    /// <summary>
    /// The resting card. Operations has no workspace hint row any more, so where the workspace
    /// cannot act the card's own disabled action states the reason and the one-step remedy —
    /// which is where an operator is already looking, and costs no reserved row.
    /// </summary>
    private static FocusCardViewModel PlaceholderCard(OperatorConsoleState state)
    {
        var hint = OperationsHint(state);
        return hint.Length == 0
            ? FocusCardViewModel.Placeholder
            : FocusCardViewModel.Placeholder with { ActionReason = hint };
    }

    private static FocusCardViewModel UnidentifiedCard(UnidentifiedSubmission pending, DateTimeOffset now) =>
        new(
            "~",
            "NO ANSWER YET",
            ElapsedText(now - pending.SubmittedAt),
            CardActions.Cancel,
            false,
            OmittedNoCancelReason,
            $"{pending.Request.Rail.ToString().ToLowerInvariant()} · {pending.Request.Amount:N2} {pending.Request.Currency}",
            $"{pending.Request.FromAccount} → {pending.Request.ToAccount}",
            $"submitted {OutcomeFeedNarrative.Clock(pending.SubmittedAt)}",
            [OmittedNoCancelReason],
            null,
            false);

    private const string OmittedNoCancelReason =
        "no transaction id yet — Omitted mode sends no key, so the bank names the payment "
        + "and the console cannot ask for it back";

    /// <summary>
    /// The card's state symbol, its state word, and the one closing block that applies. State is
    /// always symbol + word, never colour alone, and the word is never abbreviated.
    /// </summary>
    private static (string Symbol, string StateWord, IReadOnlyList<string> Closing) CardState(
        OperatorConsoleState state,
        TrackedPayment payment)
    {
        var http = $"HTTP {payment.HttpStatusCode} {payment.HttpOutcome}";
        switch (payment.State)
        {
            // Two clocks, never one: the event's own ProcessedAt and the console's observed-at
            // delta, above the legs that prove the money moved.
            case PaymentTrackingState.Settled:
                return ("●", "SETTLED", Prefixed(ClockText(payment), Legs(payment)));

            case PaymentTrackingState.Rejected:
                return ("✕", "REJECTED", Prefixed(
                    payment.Note,
                    Prefixed(ClockText(payment), [$"ErrorReason: {payment.ErrorReason ?? "(none supplied)"}"])));

            // The one payment state whose closing block holds two records instead of one,
            // because a contradiction *is* two records. Never resolved to either side.
            case PaymentTrackingState.Contradiction:
                return ("✕", "CONTRADICTED",
                [
                    $"HTTP {payment.HttpOutcome} {OutcomeFeedNarrative.PreciseClock(payment.SubmittedAt)}",
                    $"broadcast {payment.BroadcastOutcome} "
                    + $"{OutcomeFeedNarrative.PreciseClock(payment.ProcessedAt)}, observed here "
                    + $"+{ObservedDelta(payment)}",
                ]);

            // The one place the console says *who* withdrew a payment, rather than only that it
            // was withdrawn. The outcome for the money is identical either way.
            case PaymentTrackingState.Cancelled when payment.HttpOutcome == PaymentOutcome.Cancelled:
                return ("⊘", "CANCELLED BY THE RAIL", Prefixed(
                    ClockText(payment),
                    [
                        $"the instant rail ran out of time and withdrew it · {payment.HttpStatusCode} Cancelled",
                        "no money moved · safe to retry with a new key",
                    ]));

            case PaymentTrackingState.Cancelled:
                return ("⊘", "CANCELLED", Prefixed(
                    ClockText(payment),
                    [
                        "withdrawn before execution · no money moved",
                        "safe to retry with a new key",
                    ]));

            case PaymentTrackingState.OutcomeUnknown:
                return ("○", "OUTCOME UNKNOWN",
                    [payment.Note ?? "the console stopped listening", OutcomeQueryRemedy]);

            // A payment whose whole point is that nothing is coming must never borrow the
            // vocabulary of one that is waiting legitimately.
            case PaymentTrackingState.NotObserved when payment.HttpOutcome == PaymentOutcome.Ambiguous:
                return ("~", "AMBIGUOUS", ["not yet reconciled — Resend is unsafe", OutcomeQueryRemedy]);

            case PaymentTrackingState.NotObserved:
                return ("○", "OUTCOME NOT OBSERVED",
                [
                    payment.Note ?? "the console is not subscribed to transaction-events",
                    OutcomeQueryRemedy,
                ]);

            // Under injected faults a long wait is the expected result, so the card names the
            // condition rather than letting the audience read the delay as a defect.
            default:
                return ("~", "AWAITING SETTLEMENT",
                [
                    (payment.AwaitingResponse
                        ? "waiting for the bank to answer"
                        : $"submitted ──▶ {http} ──▶ waiting for the bank")
                    + AwaitingQualifier(state),
                ]);
        }
    }

    private static IReadOnlyList<string> Prefixed(string? note, IReadOnlyList<string> lines) =>
        string.IsNullOrWhiteSpace(note) ? lines : [note, .. lines];

    private static string ObservedDelta(TrackedPayment payment) =>
        payment.ProcessedAt is { } processed && payment.ObservedAt is { } observed
            ? $"{(observed - processed).TotalMilliseconds:F0} ms"
            : "an unrecorded delay";

    /// <summary>
    /// Two legs per settlement and none per rejection, so the console must never label a payment
    /// settled on both legs on the strength of one. A visibly half-settled payment is a real
    /// finding, not a rendering gap to paper over.
    /// </summary>
    private static IReadOnlyList<string> Legs(TrackedPayment payment)
    {
        var legs = payment.ObservedLegs.Select(leg => leg.ToString()).ToList();
        var summary = payment.ObservedLegs.Count switch
        {
            0 => "No balance legs observed yet.",
            1 => "1 of 2 legs observed",
            2 => string.Empty,
            var count => $"{count} legs observed — a settlement emits two",
        };
        if (summary.Length > 0)
        {
            legs.Add(summary);
        }

        return Prefixed(payment.Note, legs);
    }

    /// <summary>
    /// The card's single action slot, which is never empty and never hidden. <b>Cancel payment</b>
    /// while the payment has no proven outcome, then <b>Resend same key</b> where the key is known
    /// safe to reuse, otherwise the always-available <b>Look up outcome</b>. Cancel is not
    /// lock-exempt: it dims like every other mutating control while some other action is in
    /// flight — but never behind the submission of this very payment, whose outstanding answer is
    /// that payment's own state rather than a console action awaiting a result.
    /// </summary>
    private static (string Label, bool Enabled, string Reason) CardAction(
        OperatorConsoleState state,
        TrackedPayment payment,
        DateTimeOffset now)
    {
        // On dispatch the *action slot* re-states itself, never the state: the payment's own
        // state, clock and strip line stay exactly as they were, because asking is not an outcome.
        if (string.Equals(state.CancellingPayment, payment.TransactionId, StringComparison.Ordinal))
        {
            return (
                $"Cancelling — {ElapsedText(now - (state.CancellingSince ?? now))}",
                false,
                string.Empty);
        }

        if (payment.IsOpen)
        {
            var ownSubmission = state.ActiveMutation is { Kind: MutationKind.SubmitPayment }
                && payment.AwaitingResponse;
            return state.ActiveMutation is null || ownSubmission
                ? (CardActions.Cancel, true, string.Empty)
                : (CardActions.Cancel, false,
                    $"another action is in flight ({state.ActiveMutation.Kind} · {state.ActiveMutation.Target})");
        }

        var resendable = state.CanResendLastPayment
            && state.LastPayment is not null
            && string.Equals(state.LastPayment.IdempotencyKey, payment.TransactionId, StringComparison.Ordinal);
        if (resendable)
        {
            return state.ActiveMutation is null
                ? (CardActions.ResendSameKey, true, string.Empty)
                : (CardActions.ResendSameKey, false,
                    $"another action is in flight ({state.ActiveMutation.Kind} · {state.ActiveMutation.Target})");
        }

        // Never disabled by the single-action-in-flight lock: a read-only lookup is always
        // available, and it is the documented remedy for a feed that has been lost.
        return (CardActions.LookUpOutcome, true, string.Empty);
    }

    /// <summary>
    /// The elapsed clock while the payment is unresolved, the final clock once it is proven. A
    /// duration, never a time of day — the meta line's submit stamp is what says <i>when</i>.
    /// </summary>
    private static string CardClock(TrackedPayment payment, DateTimeOffset now) =>
        payment.IsOpen
            ? ElapsedText(now - payment.SubmittedAt)
            : ElapsedText((payment.ObservedAt ?? payment.ProcessedAt ?? payment.SubmittedAt) - payment.SubmittedAt);

    private static StillOpenRowViewModel BuildStillOpenRow(
        TrackedPayment payment,
        string? cardTransactionId,
        DateTimeOffset now)
    {
        var (symbol, status) = payment.State switch
        {
            PaymentTrackingState.OutcomeUnknown => ("○", "Outcome unknown"),
            PaymentTrackingState.NotObserved when payment.HttpOutcome == PaymentOutcome.Ambiguous =>
                ("~", "Ambiguous"),
            PaymentTrackingState.NotObserved => ("○", "Outcome not observed"),
            _ => ("~", "Awaiting"),
        };

        return new StillOpenRowViewModel(
            payment.TransactionId,
            string.Equals(payment.TransactionId, cardTransactionId, StringComparison.Ordinal),
            symbol,
            status,
            payment.Amount.ToString("N2"),
            $"{LastFour(payment.FromAccount)}→{LastFour(payment.ToAccount)}",
            payment.Rail.ToString().ToLowerInvariant(),
            ElapsedText(now - payment.SubmittedAt));
    }

    private static string LastFour(string account) =>
        account.Length <= 4 ? account : $"…{account[^4..]}";

    private const string OutcomeQueryRemedy =
        "Look up outcome is read-only and never blocked — it is the way forward from here.";

    /// <summary>
    /// The transient announcement's state-derived form. It takes the tone of the thing it
    /// announces and never a fixed one: a lost feed proves nothing about any payment, so it
    /// renders as a neutral notice rather than as a failure. A refusal the console produced
    /// itself is announced by the caller in the failure form, and is already an Evidence record
    /// by the time it is drawn.
    /// </summary>
    private static (string Text, bool IsFailure) Announcement(OperatorConsoleState state) =>
        state.Feed is { State: OutcomeFeedState.Lost } lost
            ? (OutcomeFeedNarrative.FeedLost(
                    lost.LostAt,
                    state.TrackedPayments.Count(payment => payment.State == PaymentTrackingState.OutcomeUnknown)),
                false)
            : (string.Empty, false);

    /// <summary>
    /// Under injected faults a long wait is the expected result, so the card names the condition
    /// rather than letting the audience read the delay as a defect. Empty when nothing is being
    /// injected: the region's own feed statement already says whether anyone is listening, so
    /// repeating it here would be the duplication the Stage-focus layout exists to remove.
    /// </summary>
    private static string AwaitingQualifier(OperatorConsoleState state) =>
        state.FaultsArmed && !state.Applied.IsAllZero ? " (faults in force)" : string.Empty;

    /// <summary>
    /// Two clocks, never one. Delivery latency belongs to the transport; presenting it as the
    /// bank's processing time would be the same class of lie as a written fault config reported
    /// as a live one. Null where the outcome carried no clock of its own — a proven outcome the
    /// console holds without an event stamp is a real case, not a gap to invent a figure for.
    /// </summary>
    private static string? ClockText(TrackedPayment payment)
    {
        if (payment.ProcessedAt is not { } processedAt || payment.ObservedAt is not { } observedAt)
        {
            return null;
        }

        return $"ProcessedAt {OutcomeFeedNarrative.PreciseClock(processedAt)}, observed here "
            + $"+{(observedAt - processedAt).TotalMilliseconds:F0} ms";
    }

    private static string ElapsedText(TimeSpan elapsed) =>
        elapsed < TimeSpan.Zero ? "0s" : $"{elapsed.TotalSeconds:F0}s";

    /// <summary>
    /// The fault half of a record's provenance line. Present only when something was actually
    /// being injected when it was captured, so a quiet session's rows are not padded with
    /// "none" — but a record captured under 12 seconds of injected latency can never be
    /// mistaken for one captured under none (EXPERIENCE.md, Evidence provenance).
    /// </summary>
    private static string StatusGlyph(bool succeeded) => succeeded ? "●" : "✕";

    /// <summary>
    /// A burst the operator stopped is captioned distinctly — never "drained in" — and states the
    /// unsent remainder as its own figure, so a permanently partial run is never read as payments
    /// that failed to prove themselves.
    /// </summary>
    private static string BurstCaptionLine(BurstProgress burst) =>
        burst.Cancelled
            ? $"BURST · stopped · {burst.Sent} of {burst.Requested} sent"
            : $"BURST · {burst.Requested} payments";

    /// <summary>
    /// What the API answered. Its denominator is always the run's <b>requested</b> count and
    /// never re-bases: "two hundred of two hundred sent" is one word away from "two hundred of
    /// two hundred done", and the denominator must not quietly move to make a partial run look
    /// complete.
    /// </summary>
    private static string BurstSentLine(BurstProgress burst) =>
        $"Sent  {burst.Sent} / {burst.Requested}   accepted {burst.Accepted + burst.Completed} · failed {burst.Failed}";

    /// <summary>
    /// What the broadcast proved. When the feed drops, the share the console can no longer
    /// account for leaves <c>still moving</c> and is named: leaving a count on screen with nobody
    /// listening is the same false wait the payment rows withdraw. The four figures always sum to
    /// what the Sent line accepted, so the room can do that arithmetic straight off the screen.
    /// </summary>
    private static string BurstProvenLine(BurstProgress burst) =>
        $"Settled  {burst.Settled}   rejected {burst.Rejected} · cancelled {burst.CancelledPayments} · "
        + $"still moving {burst.Awaiting} · unknown {burst.Unknown}";

    /// <summary>
    /// Printed only when it is true. A drained burst is not the same thing as a proven burst, and
    /// the longest-lived, loudest sentence this console renders must never say it is.
    /// </summary>
    private static string BurstClosingLine(BurstProgress burst)
    {
        if (burst.Requested == 0 || burst.Awaiting > 0)
        {
            return string.Empty;
        }

        // A drained burst is not the same thing as a proven burst. The unsent remainder of a
        // stopped run counts against the claim exactly as a failed send and an unknown outcome
        // do -- every payment the operator asked for and did not get an outcome for.
        var unsent = Math.Max(0, burst.Requested - burst.Sent);
        var shortfall = burst.Unknown + burst.Failed + unsent;
        if (shortfall == 0)
        {
            return "every payment proved itself · nothing left awaiting";
        }

        var noun = shortfall == 1 ? "payment has" : "payments have";
        return unsent == shortfall
            ? $"{shortfall} {noun} never been sent — see Evidence"
            : $"{shortfall} {noun} unknown outcomes — see Evidence";
    }

    private static string FaultProvenance(EvidenceRecord record) =>
        record.FaultLevels is { } levels ? $" · faults {levels}" : string.Empty;

    /// <summary>
    /// A list row shows the summary up to its first em dash and no further. Sixty-four of this
    /// console's summaries are a verdict, an em dash and a trailing clause; the clause is what
    /// makes the list unreadable from the back of a room. The whole summary survives untouched
    /// on the record, in the Details pane, in the status line and in the export — only this one
    /// projection is short.
    /// </summary>
    internal static string RowSummary(string summary)
    {
        var dash = summary.IndexOf('\u2014');
        if (dash <= 0)
        {
            return summary;
        }

        // A cut that leaves nothing identifies nothing: a row of a gutter marker and a glyph is
        // worse than a long one, so a summary that opens with its clause keeps all of itself.
        var head = summary[..dash].TrimEnd();
        return head.Length == 0 ? summary : head;
    }

    /// <summary>
    /// The whole Details pane for one record: the header block, then the payload as columns.
    /// An HTTP exchange reads REQUEST left and RESPONSE right; an inbound CloudEvent is one
    /// full-width EVENT column; anything else states in one line that it was not an HTTP
    /// exchange and keeps this console's own prose beneath it.
    /// </summary>
    internal static EvidencePaneViewModel EvidencePane(EvidenceRecord record)
    {
        var header = EvidenceHeaderText(record);
        if (record.Exchange is { } exchange)
        {
            return new EvidencePaneViewModel(
                header,
                Titled("REQUEST", RequestText(exchange, pretty: true)),
                Titled("RESPONSE", ResponseText(exchange, record.Detail, pretty: true)),
                CopyText(header, RequestText(exchange, pretty: false), ResponseText(exchange, null, pretty: false)));
        }

        if (record.Event is { } cloudEvent)
        {
            return new EvidencePaneViewModel(
                header,
                Titled("EVENT", EventText(cloudEvent, pretty: true)),
                null,
                CopyText(header, EventText(cloudEvent, pretty: false)));
        }

        // A stated absence reads as a fact; an empty pane reads as a broken console. The
        // console's own record of the action follows it, because an Aspire CLI invocation or a
        // load workflow's investigation is evidence even though it is not an exchange.
        var stated = "(this action was not an HTTP exchange, so no request or response was recorded)";
        var prose = FormatBody(record.Detail);
        var left = string.IsNullOrWhiteSpace(prose)
            ? stated
            : stated + Environment.NewLine + Environment.NewLine + prose;
        return new EvidencePaneViewModel(header, left, null, CopyText(header, record.Detail));
    }

    /// <summary>
    /// What Copy puts on the clipboard: the header block, then the raw sections, blank lines
    /// between. The header block is in every branch — a record whose timestamp, profile,
    /// duration, faults and transaction id could not be copied would be half a record — and
    /// because it is never empty, a pane that shows something can never report nothing to copy.
    /// </summary>
    private static string CopyText(string header, params string[] sections) => string.Join(
        Environment.NewLine + Environment.NewLine,
        new[] { header }.Concat(sections.Where(section => !string.IsNullOrWhiteSpace(section))));

    private static string Titled(string title, string body) => title + Environment.NewLine + body;

    /// <summary>
    /// The request as sent: request line, headers, blank, body. An outcome query and an inspect
    /// send a bare message with neither headers nor body, so their column is a request line and
    /// nothing else. That is honest and it is what a tool replaying the call would show; no
    /// header is synthesised to fill the column.
    /// </summary>
    private static string RequestText(HttpExchange exchange, bool pretty) => HttpText(
        $"{exchange.Method} {exchange.Url}",
        exchange.RequestHeaders,
        exchange.RequestBody,
        pretty,
        "(no request body was sent)");

    /// <param name="detail">
    /// The console's own account of a call that got no answer, for the pane. Passed as
    /// <see langword="null"/> for the copy text: prose is not raw response bytes, and a
    /// clipboard that carried it would not paste into a <c>.http</c> file and run.
    /// </param>
    private static string ResponseText(HttpExchange exchange, string? detail, bool pretty)
    {
        if (exchange.StatusCode is not { } statusCode)
        {
            // FR-5: a call that never got an answer carries its request and no response. There
            // is nothing raw to copy, so the copy path takes an empty section and drops it.
            if (!pretty)
            {
                return string.Empty;
            }

            var lines = new List<string> { "(no answer arrived — the request got no response)" };
            if (!string.IsNullOrWhiteSpace(detail))
            {
                lines.Add(string.Empty);
                lines.Add(FormatBody(detail));
            }

            return string.Join(Environment.NewLine, lines);
        }

        // No HTTP version: the exchange records the code and the reason phrase, and this console
        // does not print a fact it did not observe.
        var statusLine = $"HTTP {statusCode} {exchange.ReasonPhrase}".TrimEnd();
        return HttpText(
            statusLine,
            exchange.ResponseHeaders,
            exchange.ResponseBody,
            pretty,
            "(no response body was recorded)");
    }

    private static string HttpText(
        string startLine,
        IReadOnlyList<EvidenceHeader> headers,
        string? body,
        bool pretty,
        string absence)
    {
        var lines = new List<string> { startLine };
        lines.AddRange(headers.Select(header => $"{header.Name}: {header.Value}"));
        lines.Add(string.Empty);
        lines.Add(string.IsNullOrEmpty(body)
            ? pretty ? absence : string.Empty
            : pretty ? FormatBody(body) : body);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The CloudEvent as delivered: the envelope attributes the SDK actually exposes, then the
    /// data. An attribute the message did not carry is left out rather than printed empty —
    /// there is no <c>subject</c> and no <c>time</c> on a <c>TopicMessage</c>, and an invented
    /// envelope line would be worse than a short one.
    /// </summary>
    private static string EventText(CloudEventRecord cloudEvent, bool pretty)
    {
        var envelope = new List<EvidenceHeader>
        {
            new("Type", cloudEvent.Type),
            new("Id", cloudEvent.Id),
            new("Source", cloudEvent.Source),
            new("SpecVersion", cloudEvent.SpecVersion),
            new("DataContentType", cloudEvent.DataContentType),
            new("PubSubName", cloudEvent.PubSubName),
            new("Topic", cloudEvent.Topic),
            new("Path", cloudEvent.Path ?? string.Empty),
        };
        envelope.AddRange(cloudEvent.Extensions);
        return HttpText(
            "CloudEvent",
            [.. envelope.Where(attribute => attribute.Value.Length > 0)],
            cloudEvent.Data,
            pretty,
            "(the event carried no data)");
    }

    /// <summary>
    /// What happened, where, and under what conditions. Shared by the list row and the Details
    /// pane so the same record cannot read two different ways depending on which one you are
    /// looking at. The payload is not in here: it is the columns beneath, and printing it twice
    /// would be two things to point at where the demonstration needs one.
    /// </summary>
    internal static string EvidenceHeaderText(EvidenceRecord record)
    {
        var lines = new List<string>
        {
            record.Summary,
            $"{KnownTopologyProfiles.DisplayName(record.Profile)} · generation {record.RunGeneration} · {record.Timestamp:HH:mm:ss}",
            $"{record.Method} {record.Target}",
            $"HTTP {record.StatusCode?.ToString() ?? "n/a"} · {record.Duration.TotalMilliseconds:F0} ms",
            $"Faults: {FaultProvenanceDetail(record)}",
        };

        if (record.TransactionId is { Length: > 0 } transactionId)
        {
            lines.Add($"Transaction: {transactionId}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Re-indents a JSON body for reading. A single-line payload is where the interesting
    /// fields hide during a demonstration, so it is pretty-printed; anything that is not JSON
    /// is passed through untouched rather than mangled into looking like it.
    /// </summary>
    /// <remarks>
    /// Display-only. The stored <see cref="EvidenceRecord.Detail"/> and
    /// <see cref="EvidenceRecord.Exchange"/> keep the bytes as sent and as received, so the
    /// export and the clipboard carry the real payload rather than this console's
    /// reformatting of it.
    /// </remarks>
    internal static string FormatBody(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        // The payload is rarely the whole detail. A payment record is an idempotency line and
        // then the response body; an inspection is a URL and then the body. So the JSON is
        // found inside the text and reformatted in place, leaving everything around it alone.
        var start = detail.IndexOfAny(['{', '[']);
        if (start < 0)
        {
            return detail;
        }

        var bytes = Encoding.UTF8.GetBytes(detail[start..]);
        var reader = new Utf8JsonReader(
            bytes,
            new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

        try
        {
            if (!JsonDocument.TryParseValue(ref reader, out var parsed))
            {
                return detail;
            }

            using (parsed)
            {
                // Read one value and keep whatever followed it: a body is sometimes trailed by
                // a note, and dropping that would be hiding evidence rather than formatting it.
                var trailing = Encoding.UTF8.GetString(bytes[(int)reader.BytesConsumed..]);
                return detail[..start]
                    + JsonSerializer.Serialize(parsed.RootElement, IndentedJson)
                    + trailing;
            }
        }
        catch (JsonException)
        {
            // A truncated or malformed body is still evidence. Showing it verbatim is the
            // honest move; a parse failure is not licence to hide what the service sent.
            return detail;
        }
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static string FaultProvenanceDetail(EvidenceRecord record) =>
        record.FaultLevels?.ToString() ?? "none in force";

    /// <summary>
    /// Names the launch-time truth, never a bare "on": arming decides what the *next* start
    /// does and can never mean "faults are happening now".
    /// </summary>
    private static string ArmingCaption(OperatorConsoleState state)
    {
        var setting = state.FaultArmingRequested ? "armed" : "not armed";
        return state.Ownership switch
        {
            TopologyOwnership.Attached =>
                $"Faults on next start: {setting} (read-only — this AppHost is Attached)",
            TopologyOwnership.Owned =>
                $"Faults on this running AppHost: {(state.FaultsArmed ? "armed" : "not armed")} "
                + "(read-only — restart it to change)",
            _ => $"Faults {setting} on next AppHost start",
        };
    }

    /// <summary>
    /// Builds the fault chip and the Faults workspace. The chip has exactly three symbols —
    /// <c>-</c> Unavailable, <c>·</c> Armed, <c>!</c> Faults in force — and the console only
    /// reaches the third once traffic has actually carried the applied levels.
    /// </summary>
    private static FaultsViewModel BuildFaults(OperatorConsoleState state, DateTimeOffset now)
    {
        var live = state.Applied;
        var staged = state.Staged;
        var proxyRunning = IsDevProxyRunning(state);
        var available = state.FaultsArmed && proxyRunning;
        var (symbol, label) = FaultChip(state, live, now, proxyRunning);

        var knobs = new List<FaultKnobViewModel>
        {
            Knob(FaultKnobs.ErrorRate, live.ErrorRateText, staged.ErrorRateText),
            Knob(FaultKnobs.LatencyBand, live.LatencyText, staged.LatencyText),
            Knob(FaultKnobs.Throttling, live.ThrottleText, staged.ThrottleText),
        };
        var stagedCount = knobs.Count(knob => knob.IsStaged);
        var canApply = available && stagedCount > 0;
        var applyCaption = !available
            ? "Apply (unavailable)"
            : stagedCount == 0
                ? "Apply (nothing staged)"
                : $"Apply {stagedCount} staged knob{(stagedCount == 1 ? string.Empty : "s")}";

        return new FaultsViewModel(
            available,
            available
                ? string.Empty
                : state.FaultsArmed
                    ? "This topology was armed, but its Dev Proxy is not running — check the "
                      + "devproxy resource in Resources (2) and start it."
                    : OperatorConsoleController.FaultsUnavailableReason(state),
            symbol,
            label,
            knobs,
            FaultLevels.PresetsFor(state.Profile),
            staged.MatchingPresetName(state.Profile) ?? "Custom",
            canApply,
            applyCaption,
            FaultDetail(state),
            available ? CostNote : string.Empty,
            live,
            staged);
    }

    /// <summary>
    /// Stated up front, not discovered mid-talk. Applying a level restarts the Dev Proxy —
    /// the only way Dev Proxy 3.2.0 picks up a new config (ADR-019) — so calls in flight
    /// through the proxy can fail while it comes back. Panic-off pays the same cost.
    /// </summary>
    private const string CostNote =
        "Apply and 0 both restart the Dev Proxy — calls through it can fail for a moment while it comes back.";

    /// <summary>
    /// Whether a Dev Proxy is actually running in the current snapshot. The chip may only
    /// claim <c>Armed</c> or <c>Faults in force</c> when it is: intent to arm is not the same
    /// fact as a live proxy, and reporting faults against a dead one is exactly the kind of
    /// unearned confidence this console exists to avoid.
    /// </summary>
    private static bool IsDevProxyRunning(OperatorConsoleState state) =>
        state.Topology?.FindResource(KnownResources.DevProxy) is
            { Condition: ResourceCondition.Healthy or ResourceCondition.Running };

    /// <summary>
    /// The workspace's meta line: any read/write failure first, otherwise where the levels on
    /// screen came from — a config this session wrote, or the checked-in profile.
    /// </summary>
    private static string FaultDetail(OperatorConsoleState state)
    {
        if (!string.IsNullOrWhiteSpace(state.FaultDetail))
        {
            return state.FaultDetail;
        }

        if (!state.FaultsArmed)
        {
            return string.Empty;
        }

        return state.FaultLevelsFromSession
            ? "Levels read from this session's generated Dev Proxy config."
            : $"Levels read from the checked-in {KnownTopologyProfiles.DisplayName(state.Profile)} Dev Proxy profile.";
    }

    private static FaultKnobViewModel Knob(string name, string liveText, string stagedText)
    {
        var isStaged = !string.Equals(liveText, stagedText, StringComparison.Ordinal);
        return new FaultKnobViewModel(
            name,
            liveText,
            stagedText,
            // A staged number is never shown alone: a presenter glancing at the screen must
            // never read an intended level as a current one.
            isStaged ? $"{liveText} → {stagedText}" : liveText,
            isStaged);
    }

    private static (string Symbol, string Label) FaultChip(
        OperatorConsoleState state,
        FaultLevels live,
        DateTimeOffset now,
        bool proxyRunning)
    {
        if (!state.FaultsArmed)
        {
            return ("-", "Faults unavailable");
        }

        if (!proxyRunning)
        {
            // Armed at launch, but there is no live proxy to inject anything right now.
            return ("-", "Faults unavailable — Dev Proxy not running");
        }

        if (live.IsAllZero)
        {
            // An armed proxy injecting nothing must never look like an active fault.
            return ("·", "Armed");
        }

        if (state.FaultsObserved)
        {
            return ("!", "Faults in force");
        }

        // Dev Proxy owns its own reload timing. Between the write and the first intercepted
        // call carrying the new levels the console says so, never that the level is live —
        // and the readout is bounded, because a number climbing past a minute tells the
        // operator nothing the first few seconds did not.
        if (state.FaultsAppliedAt is not { } appliedAt)
        {
            return ("·", "Applied — not yet observed in traffic");
        }

        var elapsed = now - appliedAt;
        return elapsed >= FaultLevels.ObservationWindow
            ? ("·", $"Applied — still not observed after {FaultLevels.ObservationWindow.TotalSeconds:F0}s; "
                + "submit a payment, or check that Dev Proxy reloaded its config")
            : ("·", $"Applied — not yet observed in traffic ({elapsed.TotalSeconds:F0}s)");
    }

    /// <summary>
    /// Explains why the payment controls cannot act right now. Empty when they can.
    /// </summary>
    private static string OperationsHint(OperatorConsoleState state)
    {
        if (state.ActiveMutation is { } mutation)
        {
            return $"Busy — {mutation.Kind} on {mutation.Target} is in flight; controls unlock when it settles.";
        }

        return state.Profile == TopologyProfile.None || state.Ownership == TopologyOwnership.None
            ? "No topology attached — go to Resources (2) and Start or Attach a known topology first."
            : string.Empty;
    }

    /// <summary>
    /// Explains why the topology and resource controls cannot act right now. Empty when they can.
    /// </summary>
    private static string ResourcesHint(OperatorConsoleState state)
    {
        if (state.ActiveMutation is { } mutation)
        {
            return $"Busy — {mutation.Kind} on {mutation.Target} is in flight; controls unlock when it settles.";
        }

        if (state.Ownership == TopologyOwnership.None)
        {
            if (state.Preflight is null)
            {
                return "Preflight has not completed yet — running discovery.";
            }

            var failed = state.Preflight.Checks.Where(check => !check.Passed).ToList();
            if (!state.Preflight.EnvironmentReady || !state.Preflight.DiscoveryReachable)
            {
                return $"Start and Attach blocked — {string.Join(" | ", failed.Select(check => $"{check.Name}: {check.Remediation}"))}";
            }

            var blocked = KnownTopologyProfiles.All
                .Where(profile => !state.Preflight.CanStart(profile)
                    && !(state.Preflight.Profiles.TryGetValue(profile, out var candidate) && candidate.CanAttach))
                .Select(profile => $"{KnownTopologyProfiles.DisplayName(profile)}: "
                    + (state.Preflight.Profiles.TryGetValue(profile, out var candidate) ? candidate.Detail : "no preflight result"))
                .ToList();
            return blocked.Count == 0
                ? "Start or Attach a topology to enable Stop, Switch and per-resource commands."
                : string.Join(" | ", blocked);
        }

        if (!state.ResourceAuthorityAvailable)
        {
            return "Resource commands need a verified Aspire snapshot — use Refresh state.";
        }

        return state.Topology switch
        {
            null or { IsReachable: false } => "Aspire snapshot is unreachable — use Refresh state.",
            { IsFingerprintMatch: false } => "The running graph no longer matches the known profile — Stop and Start it again.",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Explains why the accepted load workflow cannot run right now. Empty when it can.
    /// </summary>
    private static string LoadHint(OperatorConsoleState state)
    {
        // Stated before Run fires, never after: a run whose conditions were injected can
        // never present as a clean run against the accepted defaults.
        var faultWarning = state.FaultsArmed && !state.Applied.IsAllZero
            ? $"Fault injection is in force ({state.Applied}) — this run's conditions are not "
              + "the accepted defaults. Press 0 to return every knob to zero first. "
            : string.Empty;
        return faultWarning + LoadHintCore(state);
    }

    private static string LoadHintCore(OperatorConsoleState state)
    {
        if (state.ActiveMutation is { } mutation)
        {
            return $"Busy — {mutation.Kind} on {mutation.Target} is in flight; the load workflow unlocks when it settles.";
        }

        if (state.Profile != TopologyProfile.LoadTests || state.Ownership == TopologyOwnership.None)
        {
            return "Load Test requires the LoadTests topology — go to Resources (2) and Start or Attach LoadTests.";
        }

        if (!state.ResourceAuthorityAvailable)
        {
            return "Load Test requires a verified Aspire snapshot — use Refresh state on the Resources workspace.";
        }

        return state.Topology?.IsReady == true
            ? string.Empty
            : "The LoadTests graph is not ready yet — waiting for every required resource to report healthy.";
    }

    private static bool CanMutateResource(OperatorConsoleState state, ResourceSnapshot resource) =>
        state.ActiveMutation is null
        && state.Ownership != TopologyOwnership.None
        && state.ResourceAuthorityAvailable
        && state.Topology is { IsReachable: true, IsFingerprintMatch: true }
        && resource.Condition is not ResourceCondition.Unknown and not ResourceCondition.Unreachable;

    private static string BuildResourceDetail(ResourceSnapshot resource)
    {
        var endpoints = resource.Endpoints.Count == 0 ? "no public endpoint" : string.Join(", ", resource.Endpoints);
        return $"{resource.Health} · replicas ×{resource.ReplicaCount} · {endpoints}{(string.IsNullOrWhiteSpace(resource.Detail) ? string.Empty : $" · {resource.Detail}")}";
    }

    private static string SymbolFor(ResourceCondition condition) => condition switch
    {
        ResourceCondition.Healthy => "●",
        ResourceCondition.Running or ResourceCondition.Starting => "~",
        ResourceCondition.Failed => "✕",
        _ => "○",
    };

    private static string LabelFor(ResourceCondition condition) => condition switch
    {
        ResourceCondition.Unreachable => "Unreachable",
        ResourceCondition.Unknown => "Unknown",
        _ => condition.ToString(),
    };

    private static string NextAction(ResourceCondition condition) => condition switch
    {
        ResourceCondition.Stopped => "Start",
        ResourceCondition.Healthy or ResourceCondition.Running => "Stop",
        ResourceCondition.Failed or ResourceCondition.Degraded => "Restart",
        _ => "Unavailable",
    };

    private static string NavigationLabel(WorkspaceKind workspace) => workspace switch
    {
        WorkspaceKind.Operations => "Operations",
        WorkspaceKind.Resources => "Resources",
        WorkspaceKind.Evidence => "Evidence/Results",
        WorkspaceKind.LoadTest => "Load Test",
        WorkspaceKind.Faults => "Faults",
        _ => workspace.ToString(),
    };

    private static string Abbreviate(string resourceName) => resourceName switch
    {
        KnownResources.PaymentsApi => "pay",
        KnownResources.CoreBankApi => "cor",
        KnownResources.Postgres => "pg",
        KnownResources.Redis => "red",
        KnownResources.Jaeger => "jae",
        KnownResources.DevProxy => "dev",
        KnownResources.LoadTestSupport => "lts",
        KnownResources.LoadTestInitializer => "ini",
        KnownResources.K6 => "k6",
        _ => resourceName.Length <= 3 ? resourceName : resourceName[..3],
    };
}
