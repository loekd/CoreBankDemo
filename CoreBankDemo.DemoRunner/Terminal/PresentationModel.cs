using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Terminal;

public sealed record NavigationItemViewModel(WorkspaceKind Workspace, string Shortcut, string Label, bool Active);

public sealed record ResourceRowViewModel(
    string Name,
    string Symbol,
    string State,
    string Detail,
    string NextAction,
    bool CanMutate);

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
    string BurstStatus,
    string LoadPhaseStatus,
    IReadOnlyList<string> LoadResults,
    bool IsBusy,
    bool CanCancelBurst,
    bool CanStopOrSwitch,
    bool CanUseLoadTest,
    bool CanResend);

public static class PresentationModelBuilder
{
    public static OperatorPresentationModel Build(OperatorConsoleState state)
    {
        var resources = (state.Topology?.Resources ?? [])
            .Select(resource =>
            {
                var nextAction = NextAction(resource.Condition);
                var supported = Enum.TryParse<ResourceCommand>(nextAction, out var command) && resource.Supports(command);
                return new ResourceRowViewModel(
                    resource.Name,
                    SymbolFor(resource.Condition),
                    LabelFor(resource.Condition),
                    BuildResourceDetail(resource),
                    supported ? nextAction : "Unavailable",
                    supported && CanMutateResource(state, resource));
            })
            .ToList();

        var evidence = state.Evidence
            .OrderByDescending(record => record.Sequence)
            .Select(record => new EvidenceRowViewModel(
                record.Sequence,
                $"{(record.Succeeded ? "●" : "✕")} {record.Summary}",
                $"{KnownTopologyProfiles.DisplayName(record.Profile)} · generation {record.RunGeneration} · {record.Timestamp:HH:mm:ss}",
                $"{record.Method} {record.Target}{Environment.NewLine}HTTP {record.StatusCode?.ToString() ?? "n/a"} · {record.Duration.TotalMilliseconds:F0} ms{Environment.NewLine}{record.Detail}",
                record.Succeeded))
            .ToList();

        var selected = state.SelectedEvidence is null
            ? "No action selected."
            : $"{state.SelectedEvidence.Summary}{Environment.NewLine}"
              + $"{KnownTopologyProfiles.DisplayName(state.SelectedEvidence.Profile)} · generation {state.SelectedEvidence.RunGeneration}{Environment.NewLine}"
              + $"{state.SelectedEvidence.Method} {state.SelectedEvidence.Target}{Environment.NewLine}"
              + $"HTTP {state.SelectedEvidence.StatusCode?.ToString() ?? "n/a"} · {state.SelectedEvidence.Duration.TotalMilliseconds:F0} ms{Environment.NewLine}"
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
        var devProxy = resources.FirstOrDefault(resource => resource.Name == KnownResources.DevProxy);
        var devProxyText = devProxy is null
            ? "DevProxy unavailable"
            : $"DevProxy {(devProxy.State is "Healthy" or "Running" ? "ON" : "OFF")}";
        var profile = KnownTopologyProfiles.DisplayName(state.Profile);
        var topologyBar = $"{profile} · {state.Ownership} · generation {state.RunGeneration} · {devProxyText} · {resourceSummary}";

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
            $"Burst {state.Burst.Sent}/{state.Burst.Requested} · accepted {state.Burst.Accepted} · completed {state.Burst.Completed} · failed {state.Burst.Failed}{(state.Burst.Cancelled ? " · Cancelled" : string.Empty)}",
            $"{state.LoadProgress.Phase} · {state.LoadProgress.Elapsed.TotalSeconds:F0}s · {state.LoadProgress.Detail}",
            loadResults,
            state.ActiveMutation is not null,
            state.ActiveMutation?.Kind == MutationKind.PaymentBurst,
            state.Ownership == TopologyOwnership.Owned && state.ActiveMutation is null,
            state.Profile == TopologyProfile.LoadTests && state.Ownership != TopologyOwnership.None && state.ActiveMutation is null,
            state.CanResendLastPayment && state.ActiveMutation is null);
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
