using CoreBankDemo.DemoRunner.Application;

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
    string BurstStatus,
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
                $"{(record.Succeeded ? "●" : "✕")} {record.Summary}",
                $"{KnownTopologyProfiles.DisplayName(record.Profile)} · generation {record.RunGeneration} · {record.Timestamp:HH:mm:ss}{FaultProvenance(record)}",
                $"{record.Method} {record.Target}{Environment.NewLine}HTTP {record.StatusCode?.ToString() ?? "n/a"} · {record.Duration.TotalMilliseconds:F0} ms{Environment.NewLine}"
                + $"Faults: {FaultProvenanceDetail(record)}{Environment.NewLine}{record.Detail}",
                record.Succeeded))
            .ToList();

        var selected = state.SelectedEvidence is null
            ? "No action selected."
            : $"{state.SelectedEvidence.Summary}{Environment.NewLine}"
              + $"{KnownTopologyProfiles.DisplayName(state.SelectedEvidence.Profile)} · generation {state.SelectedEvidence.RunGeneration}{Environment.NewLine}"
              + $"{state.SelectedEvidence.Method} {state.SelectedEvidence.Target}{Environment.NewLine}"
              + $"HTTP {state.SelectedEvidence.StatusCode?.ToString() ?? "n/a"} · {state.SelectedEvidence.Duration.TotalMilliseconds:F0} ms{Environment.NewLine}"
              + $"Faults: {FaultProvenanceDetail(state.SelectedEvidence)}{Environment.NewLine}"
              + state.SelectedEvidence.Detail;

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
            $"Burst {state.Burst.Sent}/{state.Burst.Requested} · accepted {state.Burst.Accepted} · completed {state.Burst.Completed} · failed {state.Burst.Failed}{(state.Burst.Cancelled ? " · Cancelled" : string.Empty)}",
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
    /// The fault half of a record's provenance line. Present only when something was actually
    /// being injected when it was captured, so a quiet session's rows are not padded with
    /// "none" — but a record captured under 12 seconds of injected latency can never be
    /// mistaken for one captured under none (EXPERIENCE.md, Evidence provenance).
    /// </summary>
    private static string FaultProvenance(EvidenceRecord record) =>
        record.FaultLevels is { } levels ? $" · faults {levels}" : string.Empty;

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
