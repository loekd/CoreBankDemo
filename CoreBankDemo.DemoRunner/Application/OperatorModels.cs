using CoreBankDemo.DemoRunner.Application.Doctor;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Application;

public enum WorkspaceKind
{
    Operations,
    Resources,
    Evidence,
    LoadTest,

    // Appended last on purpose: the ordinal is load-bearing (MainWindow indexes
    // its workspace array by it) and the nav rail's 1-5 keys follow it.
    Faults,
}

public enum TopologyProfile
{
    None,
    Regular,
    LoadTests,
}

public enum TopologyOwnership
{
    None,
    Owned,
    Attached,
}

public enum ResourceCondition
{
    Unknown,
    Unreachable,
    Stopped,
    Starting,
    Running,
    Healthy,
    Degraded,
    Failed,
    Completed,
}

public enum ResourceCommand
{
    Start,
    Stop,
    Restart,
}

public enum MutationKind
{
    StartTopology,
    StopTopology,
    SwitchTopology,
    ResourceCommand,
    SubmitPayment,

    /// <summary>
    /// The operator withdrawing a payment that has no proven outcome yet. Not lock-exempt: the
    /// brief exempts Cancel from <i>confirmation</i>, never from the single-action-in-flight
    /// lock, so it takes the lock exactly as a submission does.
    /// </summary>
    CancelPayment,
    PaymentBurst,
    LoadTest,
}

public enum PaymentRail
{
    Standard,
    Instant,
}

public enum IdempotencyMode
{
    Generated,
    Supplied,
    Omitted,
}

/// <summary>What HTTP proved about a submission. Never overwritten by a broadcast.</summary>
public enum PaymentOutcome
{
    Pending,
    Completed,
    Failed,
    Ambiguous,
    Rejected,
    TransportFailure,

    /// <summary>
    /// The instant rail timed out and provably withdrew the payment before it executed
    /// (<c>504</c> with <c>Status: Cancelled</c>). Nothing moved, no settlement or rejection
    /// will ever follow -- a <c>transaction.cancelled</c> broadcast may, as confirmation
    /// (ADR-020 addendum) -- and a retry with a new key is safe. A proven outcome, not a
    /// transport failure.
    /// </summary>
    Cancelled,
}

/// <summary>
/// What the console can honestly claim about a submitted payment right now, given both what
/// HTTP proved and what the broadcast said.
/// <para>
/// Deliberately separate from <see cref="PaymentOutcome"/> rather than folded into it: a
/// contradiction is only expressible when both records survive, and collapsing the two into
/// one field would force the later message to overwrite the earlier one — the exact silent
/// tie-break this console must never make.
/// </para>
/// </summary>
public enum PaymentTrackingState
{
    /// <summary>Acknowledged, feed alive, no broadcast outcome yet. Never reached by a timeout.</summary>
    Awaiting,

    /// <summary>A <c>transaction.completed</c> arrived and matched this row.</summary>
    Settled,

    /// <summary>A <c>transaction.failed</c> arrived and matched this row. A proven business outcome.</summary>
    Rejected,

    /// <summary>HTTP and the broadcast disagree. Both records stay; the console picks no winner.</summary>
    Contradiction,

    /// <summary>
    /// HTTP proved a <c>504 Cancelled</c>: the instant rail withdrew the payment before it
    /// executed. Proven by the HTTP leg alone -- no broadcast is awaited, and one that does
    /// arrive is a <see cref="Contradiction"/>.
    /// </summary>
    Cancelled,

    /// <summary>The feed dropped while this payment was outstanding. Never entered by a timeout.</summary>
    OutcomeUnknown,

    /// <summary>Submitted while nothing was listening, so nothing is awaiting anything.</summary>
    NotObserved,
}

public enum EvidenceKind
{
    Topology,
    Resource,
    Payment,
    OutcomeQuery,
    Burst,
    Inspection,
    LoadTest,
    Export,
    Fault,

    /// <summary>
    /// Something the system said rather than something the operator did — a
    /// <c>transaction-events</c> CloudEvent, or a change in the console's ability to hear them.
    /// Rendered with the inbound gutter marker (DESIGN.md, Event row).
    /// </summary>
    OutcomeEvent,
}

public enum LoadWorkflowPhase
{
    NotStarted,
    Reset,
    Run,
    Wait,
    Assert,
    Investigate,
    Completed,
    Failed,
}

public sealed record ResourceSnapshot(
    string Name,
    ResourceCondition Condition,
    string Health,
    IReadOnlyList<string> Endpoints,
    int ReplicaCount = 1,
    string? Detail = null,
    IReadOnlyList<string>? InstanceNames = null,
    string? ExecutionIdentity = null,
    IReadOnlySet<ResourceCommand>? AllowedCommands = null)
{
    public bool IsStable => Condition is ResourceCondition.Stopped
        or ResourceCondition.Healthy
        or ResourceCondition.Degraded
        or ResourceCondition.Failed
        or ResourceCondition.Completed;

    public bool Supports(ResourceCommand command) => AllowedCommands?.Contains(command) ?? true;
}

public sealed record TopologySnapshot(
    TopologyProfile Profile,
    DateTimeOffset CapturedAt,
    bool IsReachable,
    bool IsFingerprintMatch,
    string Fingerprint,
    IReadOnlyList<ResourceSnapshot> Resources,
    string? ErrorSummary = null,
    string? DashboardUrl = null)
{
    /// <summary>
    /// Whether the console actually read this graph. The parser's malformed-JSON branch answers
    /// <see cref="IsReachable"/> <c>true</c> and fabricates <c>Unknown</c> resources that accept
    /// every command, so an empty <see cref="Fingerprint"/> is the marker that separates "I read
    /// the graph and its shape is not the one I expected" from "I could not read the graph at
    /// all". Only the first of those is safe to dispatch a resource command against.
    /// </summary>
    public bool IsReadable => IsReachable && !string.IsNullOrEmpty(Fingerprint);

    /// <summary>
    /// Whether <see cref="TopologyObservationDebouncer"/> is holding this snapshot back until a
    /// second, confirming observation arrives. That is uncertainty about what is being looked
    /// at, and it still blocks resource commands — unlike a shape that merely changed.
    /// </summary>
    public bool IsAwaitingConfirmation => string.Equals(
        ErrorSummary,
        TopologyObservationDebouncer.AwaitingConfirmationSummary,
        StringComparison.Ordinal);

    public bool IsReady =>
        IsReachable
        && IsFingerprintMatch
        && KnownResources.RequiredFor(Profile).All(required =>
        {
            var resource = FindResource(required);
            return resource is not null
                && resource.ReplicaCount == KnownResources.ExpectedReplicaCount(required)
                && IsReadyCondition(required, resource.Condition);
        });

    public static TopologySnapshot Unreachable(TopologyProfile profile, DateTimeOffset capturedAt, string error) =>
        new(profile, capturedAt, false, false, string.Empty, [], error);

    public ResourceSnapshot? FindResource(string name) =>
        Resources.FirstOrDefault(resource => string.Equals(resource.Name, name, StringComparison.Ordinal));

    private static bool IsReadyCondition(string resourceName, ResourceCondition? condition) =>
        condition is ResourceCondition.Healthy or ResourceCondition.Running
        || (resourceName is KnownResources.K6 or KnownResources.LoadTestInitializer
            && condition == ResourceCondition.Completed);
}

public sealed record TopologyHandle(
    TopologyProfile Profile,
    bool IsOwned,
    int? ProcessId,
    string Fingerprint,
    string ProjectPath);

public sealed record ActiveMutation(MutationKind Kind, string Target, DateTimeOffset StartedAt);

public sealed record PaymentRequest(
    string FromAccount,
    string ToAccount,
    decimal Amount,
    string Currency,
    PaymentRail Rail);

public sealed record PaymentSubmission(
    PaymentRequest Request,
    IdempotencyMode IdempotencyMode,
    string? IdempotencyKey);

/// <summary>
/// A submission the console has sent but cannot yet name: Omitted mode sends no key, so the bank
/// names the payment and the console has no id until it answers. It is still a payment the
/// console must not leave unrepresented, so the focus card holds it with its clock running and
/// <b>Cancel payment</b> disabled with that reason stated -- never a hidden or empty action slot.
/// </summary>
public sealed record UnidentifiedSubmission(PaymentRequest Request, DateTimeOffset SubmittedAt);

public sealed record PaymentResult(
    PaymentOutcome Outcome,
    int StatusCode,
    string? PaymentId,
    string? TransactionId,
    string? ResponseStatus,
    string? Body,
    string? ErrorSummary,
    TimeSpan Duration,
    // Appended last and optional so no existing construction site moves. Carries the call home
    // exactly as it went out, for the record the console writes about it.
    HttpExchange? Exchange = null)
{
    public bool IsAmbiguous => Outcome == PaymentOutcome.Ambiguous;
}

/// <summary>
/// The body CoreBank's <c>POST /api/transactions/cancel</c> takes — its own
/// <c>TransactionRequest</c>, all five fields of which this console already holds on the
/// <see cref="TrackedPayment"/> it is cancelling. Nothing new is asked of the operator, and no
/// endpoint is added to any banking service.
/// </summary>
public sealed record PaymentCancellation(
    string FromAccount,
    string ToAccount,
    decimal Amount,
    string Currency,
    string TransactionId);

/// <summary>
/// What the bank answered a cancellation with. The console never synthesises one of these: a
/// cancel that fails, times out, or answers something unrecognised is a
/// <see cref="PaymentCancelOutcome.TransportFailure"/> and leaves the payment exactly where it was.
/// </summary>
public enum PaymentCancelOutcome
{
    /// <summary><c>200</c> with <c>Status: Cancelled</c> — the bank withdrew it before it executed.</summary>
    Cancelled,

    /// <summary>
    /// <c>200</c> carrying the committed response instead: the bank had already executed the
    /// payment, and its own answer wins over the operator's having asked.
    /// </summary>
    AlreadyCommitted,

    /// <summary>
    /// <c>409</c> — the bank refuses to withdraw the row now and its body carries that row's
    /// current status. The console prints that status and never a sentence about what the bank
    /// is doing.
    /// </summary>
    Refused,

    /// <summary>
    /// Timeout, connection failure, any other status code, or a body the console cannot read.
    /// Asserts nothing about the payment.
    /// </summary>
    TransportFailure,
}

/// <param name="Status">The status word the bank's body carried, verbatim. Null when there was none.</param>
/// <param name="ProcessedAt">
/// The bank's own clock for the outcome it just stated, when its body carried one. Kept apart
/// from the console's observed-at time, because delivery latency belongs to the transport and
/// presenting it as the bank's processing time would be a lie of the same class as claiming a
/// written fault config is a live fault.
/// </param>
public sealed record PaymentCancellationResult(
    PaymentCancelOutcome Outcome,
    int StatusCode,
    string? TransactionId,
    string? Status,
    string? Body,
    string? ErrorSummary,
    TimeSpan Duration,
    DateTimeOffset? ProcessedAt = null,
    HttpExchange? Exchange = null);

public sealed record InspectionResult(
    bool Succeeded,
    int StatusCode,
    string Target,
    string? Body,
    string? ErrorSummary,
    TimeSpan Duration,
    HttpExchange? Exchange = null);

/// <summary>
/// One header line, recorded as it was set or as it came back. Never interpreted: a header the
/// console set is shown verbatim, and a header it did not set is absent rather than explained.
/// </summary>
public sealed record EvidenceHeader(string Name, string Value);

/// <summary>
/// One outbound HTTP call as it happened — the request as sent, the answer as received. Read
/// top to bottom it is a raw exchange, which is what the audience's prior (Postman, a
/// <c>.http</c> file) already knows how to read.
/// </summary>
/// <param name="StatusCode">
/// Null when no answer ever arrived — a timeout or a dead connection. The absence is the fact,
/// and the pane states it rather than rendering a blank column.
/// </param>
public sealed record HttpExchange(
    string Method,
    string Url,
    IReadOnlyList<EvidenceHeader> RequestHeaders,
    string? RequestBody,
    int? StatusCode,
    string? ReasonPhrase,
    IReadOnlyList<EvidenceHeader> ResponseHeaders,
    string? ResponseBody);

/// <summary>
/// One <c>transaction-events</c> CloudEvent as Dapr delivered it: every envelope attribute
/// <c>TopicMessage</c> actually exposes, plus the data payload verbatim.
/// <para>
/// There is deliberately no <c>subject</c> and no <c>time</c>: the SDK does not project them
/// onto the message, and an invented envelope line would be worse than a short one. The
/// console's own receive clock is on <see cref="EvidenceRecord.Timestamp"/> and the two are
/// never presented as the same thing.
/// </para>
/// </summary>
public sealed record CloudEventRecord(
    string Id,
    string Source,
    string Type,
    string SpecVersion,
    string DataContentType,
    string PubSubName,
    string Topic,
    string? Path,
    IReadOnlyList<EvidenceHeader> Extensions,
    string Data);

public sealed record EvidenceRecord(
    long Sequence,
    DateTimeOffset Timestamp,
    TopologyProfile Profile,
    int RunGeneration,
    EvidenceKind Kind,
    string Summary,
    string Method,
    string Target,
    int? StatusCode,
    TimeSpan Duration,
    string Detail,
    bool Succeeded,
    // Provenance, for the same reason the topology is: a 202 captured under 12
    // seconds of injected latency and one captured under none are different facts.
    FaultLevels? FaultLevels = null,
    // The only correlation identifier in this console. Present on payment records and on
    // every inbound event that carried one, so the Evidence feed can be read alongside an
    // Operations row without a second lookup. Null for records that belong to no transaction.
    string? TransactionId = null,
    // The call this record is about, as sent and as answered. Only the four one-record-one-call
    // sites attach one; a burst or a load workflow is an aggregate over many and attaches none.
    HttpExchange? Exchange = null,
    // The CloudEvent this record is about, as delivered. Only ever on an OutcomeEvent record.
    CloudEventRecord? Event = null);

/// <summary>
/// One <c>com.corebank.account.balance.updated</c> leg. Two per settlement, none per
/// rejection — the console renders the counts it was given, never a guess about them.
/// </summary>
public sealed record SettlementLeg(
    string AccountNumber,
    decimal Delta,
    decimal NewBalance,
    string Currency,
    DateTimeOffset ObservedAt)
{
    /// <summary>
    /// The most literal proof available that money moved, and the one a room reads fastest:
    /// <c>1001  −250.00 → 4,750.00 EUR</c>. Rendered identically in Operations and in the
    /// Evidence feed so the two can be read against each other.
    /// </summary>
    public override string ToString() =>
        $"{AccountNumber}  {(Delta < 0 ? "−" : "+")}{Math.Abs(Delta):N2} → {NewBalance:N2} {Currency}";
}

/// <summary>
/// A payment this console submitted and is now watching. Rows resolve <b>in place</b>: the
/// list is ordered by <see cref="Sequence"/> and never re-sorted, because an arriving outcome
/// must never move a row the operator may have a finger on.
/// </summary>
/// <param name="HttpOutcome">What the submission's own response proved. Never overwritten.</param>
/// <param name="BroadcastOutcome">
/// What the broadcast said, once it said anything. Kept beside <paramref name="HttpOutcome"/>
/// rather than replacing it, so a disagreement stays visible as a disagreement.
/// </param>
/// <param name="ProcessedAt">The event's own clock. Printed separately from <paramref name="ObservedAt"/>.</param>
/// <param name="ObservedAt">The console's clock when the event arrived. Delivery time, not processing time.</param>
public sealed record TrackedPayment(
    long Sequence,
    string TransactionId,
    PaymentRail Rail,
    decimal Amount,
    string Currency,
    string FromAccount,
    string ToAccount,
    DateTimeOffset SubmittedAt,
    PaymentOutcome HttpOutcome,
    int HttpStatusCode,
    PaymentTrackingState State,
    PaymentOutcome? BroadcastOutcome = null,
    DateTimeOffset? ProcessedAt = null,
    DateTimeOffset? ObservedAt = null,
    string? ErrorReason = null,
    IReadOnlyList<SettlementLeg>? Legs = null,
    string? Note = null,
    // True between the Submit keypress and the bank's own answer. The card is occupied from the
    // instant Submit is pressed, not from the instant an answer arrives: a payment the console
    // has sent and cannot yet describe is precisely the payment it must not leave unrepresented,
    // and its id is already known (the id *is* the idempotency key), so Cancel payment is live
    // for the whole of an instant payment's budget rather than only after it resolves.
    bool AwaitingResponse = false,
    // The key this payment was submitted under, where there was one. Normally identical to
    // TransactionId -- the id *is* the idempotency key -- and kept separately only so a row the
    // console created at the Submit keypress can still be found after a service answered with a
    // different id, and so a resend lands on the row it already has.
    string? IdempotencyKey = null)
{
    public IReadOnlyList<SettlementLeg> ObservedLegs => Legs ?? [];

    /// <summary>
    /// True while a broadcast outcome could still legitimately arrive for this row. Only
    /// these rows are re-labelled when the feed drops.
    /// </summary>
    public bool IsOutstanding => State == PaymentTrackingState.Awaiting;

    /// <summary>
    /// True while this payment has no proven outcome — the STILL OPEN strip's membership rule,
    /// and the rule that decides whether <b>Cancel payment</b> is the card's action. Wider than
    /// <see cref="IsOutstanding"/> on purpose: <c>Outcome unknown</c> and <c>Outcome not
    /// observed</c> are not proof of anything, and they are the payments the operator most needs
    /// listed. A payment leaves only when something proved it finished.
    /// </summary>
    public bool IsOpen => State is PaymentTrackingState.Awaiting
        or PaymentTrackingState.OutcomeUnknown
        or PaymentTrackingState.NotObserved;
}

/// <summary>
/// The burst's two legs, never merged: the HTTP leg is what the API answered, the proven leg
/// is what the broadcast confirmed. <see cref="Awaiting"/> is computed rather than stored,
/// which makes it structurally impossible for a timeout to decrement it.
/// </summary>
public sealed record BurstProgress(
    int Requested,
    int Sent,
    int Accepted,
    int Completed,
    int Failed,
    bool Cancelled,
    // Proven leg -- only ever moved by a received event.
    int Settled = 0,
    int Rejected = 0,
    // Withdrawn, not resolved: the share of the proven leg the console stopped being able to
    // observe when the feed dropped. It is never moved by a timeout, only by feed loss.
    int Unknown = 0,
    // Withdrawn payments, both legs summed: instant payments the rail timed out and provably
    // withdrew (504 Cancelled, the HTTP leg) plus accepted payments CoreBank later withdrew and
    // said so by broadcast (the proven leg, ADR-020 addendum). Never a failure, and never
    // counted as Rejected. Distinct from <see cref="Cancelled"/>, which is the operator
    // aborting the burst itself.
    int CancelledPayments = 0,
    // Proven leg only: the share of <see cref="CancelledPayments"/> that arrived as a
    // <c>transaction.cancelled</c> broadcast for an accepted submission. Kept apart from the
    // HTTP leg's 504s -- which were never accepted, so must not drain <see cref="Outstanding"/>
    // -- so a broadcast cancellation resolves a waiting payment exactly as a settlement would.
    int CancelledByBroadcast = 0)
{
    public static BurstProgress Empty => new(0, 0, 0, 0, 0, false);

    /// <summary>
    /// How many of this burst's submissions could still legitimately produce an outcome.
    /// </summary>
    public int Outstanding => Math.Max(0, Accepted + Completed - Settled - Rejected - CancelledByBroadcast - Unknown);

    /// <summary>
    /// A count, never a countdown: it goes up as submissions are accepted and down only as
    /// events arrive. Draining to zero is the burst's visual confirmation. Only ids whose HTTP
    /// leg was accepted or completed are ever registered for the proven leg, so this cannot be
    /// driven negative by a broadcast the HTTP leg never counted.
    /// </summary>
    public int Awaiting => Outstanding;
}

public sealed record InvariantResult(string Name, bool Passed, string Detail);

public sealed record InlineSettlementResult(
    bool Observed,
    string Detail,
    int Count = 0,
    bool ThresholdPassed = false);

public sealed record LoadWorkflowProgress(LoadWorkflowPhase Phase, TimeSpan Elapsed, string Detail);

public sealed record LoadWorkflowResult(
    bool Completed,
    bool AllPassed,
    LoadWorkflowPhase FinalPhase,
    IReadOnlyList<InvariantResult> Invariants,
    InlineSettlementResult InlineSettlement,
    string InvestigationDetail,
    string? ErrorSummary)
{
    public static LoadWorkflowResult Success(
        IReadOnlyList<InvariantResult> invariants,
        InlineSettlementResult inlineSettlement,
        string investigationDetail) =>
        new(
            true,
            invariants.All(invariant => invariant.Passed) && inlineSettlement.Observed,
            LoadWorkflowPhase.Completed,
            invariants,
            inlineSettlement,
            investigationDetail,
            null);

    public static LoadWorkflowResult Failure(
        LoadWorkflowPhase phase,
        string error,
        IReadOnlyList<InvariantResult>? invariants = null,
        string investigationDetail = "",
        InlineSettlementResult? inlineSettlement = null) =>
        new(
            false,
            false,
            phase,
            invariants ?? [],
            inlineSettlement ?? new InlineSettlementResult(false, "Not reported by the accepted harness."),
            investigationDetail,
            error);
}

public sealed record OperatorConsoleState(
    WorkspaceKind ActiveWorkspace,
    TopologyProfile Profile,
    TopologyOwnership Ownership,
    int RunGeneration,
    TopologySnapshot? Topology,
    DoctorReport? Preflight,
    bool ResourceAuthorityAvailable,
    ActiveMutation? ActiveMutation,
    IReadOnlyList<EvidenceRecord> Evidence,
    EvidenceRecord? SelectedEvidence,
    PaymentSubmission? LastPayment,
    bool CanResendLastPayment,
    BurstProgress Burst,
    LoadWorkflowProgress LoadProgress,
    LoadWorkflowResult? LastLoadResult,
    string StatusLine,
    // --- Fault injection -------------------------------------------------
    // Arming is a launch-time property: this flag decides what the *next* start
    // does, and is never a live on/off switch for a running topology. On by
    // default (ADR-020) so Dev Proxy is always up in the Resources workspace and
    // the fault sliders work without a restart -- devproxy is therefore a Start-time
    // prerequisite unless the operator explicitly unarms. Fault levels still default
    // to AllZero, so nothing is actually injected until a knob is staged and applied.
    bool FaultArmingRequested = true,
    // True only when this session started the current topology with a Dev Proxy.
    // An Attached topology can be reported on, never re-armed.
    bool FaultsArmed = false,
    FaultLevels? AppliedFaults = null,
    FaultLevels? StagedFaults = null,
    DateTimeOffset? FaultsAppliedAt = null,
    // A written config is not a live fault. Only traffic carrying the levels
    // flips this, and only then does the chip read "Faults in force".
    bool FaultsObserved = false,
    // True when the levels came from a session config this console wrote, false when
    // they were read from the checked-in profile the AppHost started with.
    bool FaultLevelsFromSession = false,
    string FaultDetail = "")
{
    /// <summary>
    /// Payments submitted from this console, oldest first. Only single submissions get a row;
    /// a burst's outcomes are counted in <see cref="Burst"/> rather than followed one by one.
    /// </summary>
    public IReadOnlyList<TrackedPayment> TrackedPayments { get; init; } = [];

    /// <summary>
    /// The transaction id of the payment the operator selected, or null while they have selected
    /// none. Selection drives the focus card and nothing else does; an arriving event never
    /// changes it. Held as an id rather than as a copy of the row — the precedent
    /// <see cref="SelectedEvidence"/> sets does not apply, because an evidence record is immutable
    /// while a payment resolves <i>in place</i> and a captured copy would go stale on the largest
    /// object on the screen.
    /// </summary>
    public string? SelectedPayment { get; init; }

    /// <summary>
    /// The Omitted-mode submission currently in flight, if any. It has no transaction id to be
    /// tracked by, and the card says so rather than dropping it.
    /// </summary>
    public UnidentifiedSubmission? UnidentifiedSubmission { get; init; }

    /// <summary>
    /// The payment a cancel is currently in flight for, and when it was dispatched. On dispatch
    /// the card's <i>action slot</i> — never its state — re-states itself as
    /// <c>Cancelling — 3s</c>, while the payment's own state, clock and strip line stay exactly
    /// as they were: asking is not an outcome.
    /// </summary>
    public string? CancellingPayment { get; init; }

    public DateTimeOffset? CancellingSince { get; init; }

    /// <summary>
    /// Whether this console can currently hear the broadcast. Carried on the rows that depend
    /// on it and in the Evidence feed header, never as a chip.
    /// </summary>
    public OutcomeFeedStatus Feed { get; init; } = OutcomeFeedStatus.NotStarted;

    public FaultLevels Applied => AppliedFaults ?? FaultLevels.AllZero;

    public FaultLevels Staged => StagedFaults ?? Applied;

    public bool HasStagedFaultChange => Staged != Applied;

    public static OperatorConsoleState Empty => new(
        WorkspaceKind.Operations,
        TopologyProfile.None,
        TopologyOwnership.None,
        0,
        null,
        null,
        false,
        null,
        [],
        null,
        null,
        false,
        BurstProgress.Empty,
        new LoadWorkflowProgress(LoadWorkflowPhase.NotStarted, TimeSpan.Zero, "Not run this session."),
        null,
        "No topology active. Select Regular or LoadTests to start or attach.");
}

public sealed record OperatorConsoleOptions
{
    public int MaximumEvidenceRecords { get; init; } = 500;

    /// <summary>
    /// Bounds the Operations payment list the same way evidence is bounded. Older rows are
    /// dropped, never summarised into a claim -- the Evidence feed keeps their records.
    /// </summary>
    public int MaximumTrackedPayments { get; init; } = 100;
    public int MinimumBurstCount { get; init; } = 1;
    public int MaximumBurstCount { get; init; } = 500;
    public int MinimumBurstConcurrency { get; init; } = 1;
    public int MaximumBurstConcurrency { get; init; } = 20;
    public TimeSpan SnapshotFreshness { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan TransitionTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}
