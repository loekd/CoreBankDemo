using CoreBankDemo.DemoRunner.Application.Doctor;

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

public enum PaymentOutcome
{
    Pending,
    Completed,
    Failed,
    Ambiguous,
    Rejected,
    TransportFailure,
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
    FaultLevels? FaultLevels = null);

public sealed record BurstProgress(
    int Requested,
    int Sent,
    int Accepted,
    int Completed,
    int Failed,
    bool Cancelled)
{
    public static BurstProgress Empty => new(0, 0, 0, 0, 0, false);
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
    public int MinimumBurstCount { get; init; } = 1;
    public int MaximumBurstCount { get; init; } = 500;
    public int MinimumBurstConcurrency { get; init; } = 1;
    public int MaximumBurstConcurrency { get; init; } = 20;
    public TimeSpan SnapshotFreshness { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan TransitionTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}
