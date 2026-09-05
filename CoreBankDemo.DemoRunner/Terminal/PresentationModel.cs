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

public sealed record EvidenceRowViewModel(
    long Sequence,
    string Summary,
    string Provenance,
    string Detail,
    bool Succeeded);

/// <summary>
/// One submitted payment as the Operations list renders it. The list is projected in
/// submission order and never re-sorted, so an arriving outcome updates the row in the
/// position it already occupies.
/// </summary>
/// <param name="Headline">Bold verb/object line — the status label is part of it, never colour alone.</param>
/// <param name="Meta">
/// Muted detail beneath the headline. Carries the two clocks as separate figures and, for an
/// awaiting row, the inline <c>(listening)</c> qualifier.
/// </param>
/// <param name="Legs">The balance legs observed so far, in a fixed column.</param>
/// <param name="LegSummary">
/// States a half-settled payment out loud (<c>1 of 2 legs observed</c>). A visibly half-settled
/// payment is a real finding, not a rendering gap to paper over.
/// </param>
/// <param name="Remedy">The one-step way forward when the console cannot know the outcome.</param>
public sealed record PaymentRowViewModel(
    long Sequence,
    string TransactionId,
    string Symbol,
    string Headline,
    string Meta,
    IReadOnlyList<string> Legs,
    string LegSummary,
    string Remedy);

public sealed record OperatorPresentationModel(
    string TopologyBar,
    IReadOnlyList<NavigationItemViewModel> Navigation,
    WorkspaceKind ActiveWorkspace,
    IReadOnlyList<ResourceRowViewModel> Resources,
    IReadOnlyList<EvidenceRowViewModel> Evidence,
    string EvidenceStrip,
    string SelectedEvidenceDetail,
    string MutationStatus,
    FaultsViewModel Faults,
    IReadOnlyList<PaymentRowViewModel> Payments,
    string FeedStatus,
    string BurstStatus,
    string BurstProvenStatus,
    string LoadPhaseStatus,
    IReadOnlyList<string> LoadResults,
    bool IsBusy,
    bool CanCancelBurst,
    bool CanStopOrSwitch,
    bool CanUseLoadTest,
    bool CanResend,
    string OperationsHint,
    string ResourcesHint,
    string LoadHint,
    string ArmingCaption,
    bool CanChangeArming);

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
                    ? $"< {StatusGlyph(record.Succeeded)} {record.Summary}"
                    : $"  {StatusGlyph(record.Succeeded)} {record.Summary}",
                $"{KnownTopologyProfiles.DisplayName(record.Profile)} · generation {record.RunGeneration} · {record.Timestamp:HH:mm:ss}{FaultProvenance(record)}",
                EvidenceDetailText(record),
                record.Succeeded))
            .ToList();

        // One projection, used by both the row and the Details pane. They were built separately
        // and had already drifted: the pane omitted the timestamp the row showed, so the same
        // record read differently depending on where you looked at it.
        var selected = state.SelectedEvidence is null
            ? "No action selected. Select a row on the left, or press Details."
            : EvidenceDetailText(state.SelectedEvidence);

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

        var payments = state.TrackedPayments
            .Select(payment => BuildPaymentRow(state, payment, now))
            .ToList();

        var resourceSummary = resources.Count == 0
            ? "resources ○ Unknown"
            : string.Join(" ", resources.Select(resource => $"{Abbreviate(resource.Name)} {resource.Symbol}"));
        var faults = BuildFaults(state, now);
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
            state.Evidence.LastOrDefault() is { } latest
                ? $"{KnownTopologyProfiles.DisplayName(latest.Profile)} · generation {latest.RunGeneration} · {latest.Summary}"
                : "No actions yet this session.",
            selected,
            state.ActiveMutation is null
                ? state.StatusLine
                : $"{state.ActiveMutation.Kind} · {state.ActiveMutation.Target} · Running",
            faults,
            payments,
            FeedStatusLine(state),
            // The two legs are never merged: a burst is exactly where "acknowledged" and
            // "finished" diverge.
            $"HTTP leg · Burst {state.Burst.Sent}/{state.Burst.Requested} · accepted {state.Burst.Accepted} · completed {state.Burst.Completed} · failed {state.Burst.Failed}{(state.Burst.Cancelled ? " · Cancelled" : string.Empty)}",
            BurstProvenLine(state.Burst),
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
            OperationsHint(state),
            ResourcesHint(state),
            LoadHint(state),
            ArmingCaption(state),
            state.Ownership == TopologyOwnership.None);
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
    /// Projects one tracked payment. Every state is carried in the row's own text, so it
    /// survives a monochrome terminal, and the elapsed readout is only ever attached to a row
    /// that is genuinely still waiting.
    /// </summary>
    private static PaymentRowViewModel BuildPaymentRow(
        OperatorConsoleState state,
        TrackedPayment payment,
        DateTimeOffset now)
    {
        var legs = payment.ObservedLegs.Select(leg => leg.ToString()).ToList();
        var http = $"HTTP {payment.HttpStatusCode} {payment.HttpOutcome}";
        var request = $"{payment.Rail.ToString().ToLowerInvariant()} {payment.Amount:N2} {payment.Currency} "
            + $"{payment.FromAccount} → {payment.ToAccount}";

        // Meta lines are ordered by what must survive a narrow terminal: the qualifier, the
        // clocks and the ErrorReason lead, and the request detail trails, because a row is
        // truncated at its right edge and those are the parts read aloud
        // (EXPERIENCE.md, Responsive & Platform).
        var (symbol, headline, meta, remedy) = payment.State switch
        {
            PaymentTrackingState.Awaiting => (
                "~",
                $"Awaiting settlement — {payment.TransactionId}",
                // The (listening) qualifier is part of the same string as the elapsed time, so
                // the wait is never ambiguous about *who* is waiting.
                $"{ElapsedText(now - payment.SubmittedAt)} ({AwaitingQualifier(state)}) · {http} · {request}",
                string.Empty),

            PaymentTrackingState.Settled => (
                "●",
                $"Settled — {payment.TransactionId}",
                $"{ClockText(payment)} · {http} · {request}",
                string.Empty),

            PaymentTrackingState.Rejected => (
                "✕",
                $"Rejected — {payment.TransactionId}",
                $"ErrorReason: {payment.ErrorReason ?? "(none supplied)"} · {ClockText(payment)} · "
                + $"{http} · {request}",
                string.Empty),

            // Both records stay on screen, both stay labelled with their source and time. The
            // console has no tie-break rule and should never acquire one.
            PaymentTrackingState.Contradiction => (
                "✕",
                $"Contradiction — {payment.Note ?? "HTTP and the broadcast disagree"} — {payment.TransactionId}",
                $"HTTP said {payment.HttpOutcome} ({payment.HttpStatusCode}) at "
                + $"{OutcomeFeedNarrative.Clock(payment.SubmittedAt)} · "
                + $"broadcast said {payment.BroadcastOutcome} at "
                + $"{OutcomeFeedNarrative.Clock(payment.ProcessedAt)}, observed here "
                + $"{OutcomeFeedNarrative.Clock(payment.ObservedAt)} · {request}",
                OutcomeQueryRemedy),

            PaymentTrackingState.OutcomeUnknown => (
                "○",
                $"Outcome unknown — {payment.Note ?? "the console stopped listening"} — {payment.TransactionId}",
                $"{http} · {request}",
                OutcomeQueryRemedy),

            _ => (
                "○",
                $"Outcome not observed — no feed — {payment.TransactionId}",
                $"{payment.Note ?? "the console is not subscribed to transaction-events"} · {http} · {request}",
                OutcomeQueryRemedy),
        };

        return new PaymentRowViewModel(
            payment.Sequence,
            payment.TransactionId,
            symbol,
            headline,
            meta,
            legs,
            LegSummary(payment),
            remedy);
    }

    private const string OutcomeQueryRemedy =
        "Query outcome with this transaction id — it is read-only and never blocked. "
        + "Select this row and leave the outcome field blank to use it.";

    /// <summary>
    /// Two legs per settlement and none per rejection, so the console must never label a
    /// payment settled on both legs on the strength of one.
    /// </summary>
    private static string LegSummary(TrackedPayment payment) => payment.State switch
    {
        PaymentTrackingState.Rejected => "No balance legs — a rejection emits none.",
        _ => payment.ObservedLegs.Count switch
        {
            0 => payment.State == PaymentTrackingState.Settled ? "No balance legs observed yet." : string.Empty,
            1 => "1 of 2 legs observed",
            // Two legs is the whole settlement; the aligned amounts say it better than a label.
            2 => string.Empty,
            var count => $"{count} legs observed — a settlement emits two",
        },
    };

    /// <summary>
    /// Under injected faults a long wait is the expected result, so the row names the condition
    /// rather than letting the audience read the delay as a defect.
    /// </summary>
    private static string AwaitingQualifier(OperatorConsoleState state) =>
        state.FaultsArmed && !state.Applied.IsAllZero ? "listening, faults in force" : "listening";

    /// <summary>
    /// Two clocks, never one. Delivery latency belongs to the transport; presenting it as the
    /// bank's processing time would be the same class of lie as a written fault config reported
    /// as a live one.
    /// </summary>
    private static string ClockText(TrackedPayment payment)
    {
        if (payment.ProcessedAt is not { } processedAt || payment.ObservedAt is not { } observedAt)
        {
            return "no event clocks recorded";
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
    /// The burst's proven leg. When the feed drops, the share it can no longer account for
    /// moves out of <c>awaiting</c> and is named: leaving "awaiting 12" on screen with nobody
    /// listening is the same false wait the payment rows withdraw.
    /// </summary>
    private static string BurstProvenLine(BurstProgress burst)
    {
        var line = $"Proven leg · settled {burst.Settled} · rejected {burst.Rejected} · awaiting {burst.Awaiting}";
        return burst.Unknown > 0 ? $"{line} · outcome unknown {burst.Unknown}" : line;
    }

    private static string FaultProvenance(EvidenceRecord record) =>
        record.FaultLevels is { } levels ? $" · faults {levels}" : string.Empty;

    /// <summary>
    /// The full readout for one evidence record: what happened, where, under what conditions,
    /// then the payload. Shared by the list row and the Details pane so the same record cannot
    /// read two different ways depending on which one you are looking at.
    /// </summary>
    internal static string EvidenceDetailText(EvidenceRecord record)
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

        var body = FormatBody(record.Detail);
        lines.Add(string.Empty);
        // Say so rather than trailing off into blank space: an empty pane reads as a broken
        // console, while "no body" is a fact about the response.
        lines.Add(string.IsNullOrWhiteSpace(body) ? "(no response body was recorded)" : body);

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Re-indents a JSON body for reading. A single-line payload is where the interesting
    /// fields hide during a demonstration, so it is pretty-printed; anything that is not JSON
    /// is passed through untouched rather than mangled into looking like it.
    /// </summary>
    /// <remarks>
    /// Display-only. The stored <see cref="EvidenceRecord.Detail"/> keeps the bytes the service
    /// actually returned, already redacted, so the export and the clipboard still carry the
    /// real payload rather than this console's reformatting of it.
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
