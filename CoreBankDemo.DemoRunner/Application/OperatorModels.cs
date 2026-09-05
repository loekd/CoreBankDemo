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

public sealed record PaymentResult(
    PaymentOutcome Outcome,
    int StatusCode,
    string? PaymentId,
    string? TransactionId,
    string? ResponseStatus,
    string? Body,
    string? ErrorSummary,
    TimeSpan Duration)
{
    public bool IsAmbiguous => Outcome == PaymentOutcome.Ambiguous;
}

public sealed record InspectionResult(
    bool Succeeded,
    int StatusCode,
    string Target,
    string? Body,
    string? ErrorSummary,
    TimeSpan Duration);

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
    // every inbound event, so the Evidence feed can be read alongside an Operations row
    // without a second lookup. Null for records that belong to no transaction.
    string? TransactionId = null);

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
    string? Note = null)
{
    public IReadOnlyList<SettlementLeg> ObservedLegs => Legs ?? [];

    /// <summary>
    /// True while a broadcast outcome could still legitimately arrive for this row. Only
    /// these rows are re-labelled when the feed drops.
    /// </summary>
    public bool IsOutstanding => State == PaymentTrackingState.Awaiting;
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
    int Unknown = 0)
{
    public static BurstProgress Empty => new(0, 0, 0, 0, 0, false);

    /// <summary>
    /// How many of this burst's submissions could still legitimately produce an outcome.
    /// </summary>
    public int Outstanding => Math.Max(0, Accepted + Completed - Settled - Rejected - Unknown);

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
    // does, and is never a live on/off switch for a running topology. Off by
    // default because Dev Proxy is opt-in -- defaulting on would make the binary
    // a hard prerequisite for every console-started topology.
    bool FaultArmingRequested = false,
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
