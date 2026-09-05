using System.Collections.Concurrent;
using CoreBankDemo.DemoRunner.Application.Doctor;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Application;

public sealed record CommandResult(bool Succeeded, string Message)
{
    public static CommandResult Ok(string message) => new(true, message);
    public static CommandResult Rejected(string message) => new(false, message);
}

internal sealed record EvidenceProvenance(TopologyProfile Profile, int RunGeneration, FaultLevels? Faults);
internal sealed record OperationContext(TopologyProfile Profile, int RunGeneration, string Fingerprint, FaultLevels? Faults);

public sealed class OperatorConsoleController
{
    private readonly IAspireAdapter _aspire;
    private readonly IProcessAdapter _processes;
    private readonly IPaymentGateway _payments;
    private readonly ILoadWorkflowRunner _loadWorkflow;
    private readonly IEvidenceExporter _exporter;
    private readonly IFaultInjector _faults;
    private readonly IBrowserLauncher _browser;
    private readonly IPreflightRunner _preflight;
    private readonly TimeProvider _time;
    private readonly OperatorConsoleOptions _options;
    private readonly Func<string> _keyFactory;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly object _sync = new();
    private readonly TopologyObservationDebouncer _debouncer = new();
    private readonly SemaphoreSlim _faultCommitGate = new(1, 1);

    private OperatorConsoleState _state = OperatorConsoleState.Empty;
    private TopologyHandle? _ownedHandle;
    private CancellationTokenSource? _burstCancellation;
    private TaskCompletionSource? _activeMutationCompletion;
    private bool _shutdownRequested;
    private long _evidenceSequence;
    private int _burstSequence;

    public OperatorConsoleController(
        IAspireAdapter aspire,
        IProcessAdapter processes,
        IPaymentGateway payments,
        ILoadWorkflowRunner loadWorkflow,
        IEvidenceExporter exporter,
        IFaultInjector faults,
        IBrowserLauncher browser,
        IPreflightRunner preflight,
        TimeProvider time,
        OperatorConsoleOptions? options = null,
        Func<string>? keyFactory = null)
    {
        _aspire = aspire;
        _processes = processes;
        _payments = payments;
        _loadWorkflow = loadWorkflow;
        _exporter = exporter;
        _faults = faults;
        _browser = browser;
        _preflight = preflight;
        _time = time;
        _options = options ?? new OperatorConsoleOptions();
        _keyFactory = keyFactory ?? (() => Guid.NewGuid().ToString("D"));
    }

    public event Action<OperatorConsoleState>? StateChanged;

    public OperatorConsoleState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public int? OwnedProcessId
    {
        get
        {
            lock (_sync)
            {
                return _ownedHandle?.ProcessId;
            }
        }
    }

    public bool CanRunLoadTest
    {
        get
        {
            var state = State;
            return state.Profile == TopologyProfile.LoadTests
                && state.Topology?.IsReady == true
                && HasFreshResourceAuthority(state)
                && state.ActiveMutation is null;
        }
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var preflight = await _preflight.RunAsync(State.FaultArmingRequested, ct);
        var discovered = preflight.Profiles.Values
            .Select(profile => profile.Snapshot)
            .Where(snapshot => snapshot is not null)
            .Cast<TopologySnapshot>()
            .ToList();
        var matches = discovered.Where(snapshot => snapshot.IsReady).ToList();

        if (!preflight.DiscoveryReachable)
        {
            Update(state => state with
            {
                Preflight = preflight,
                ResourceAuthorityAvailable = false,
                StatusLine = $"Aspire discovery Unreachable — {preflight.Checks.First(check => check.Name == "Aspire discovery").Remediation}",
            });
        }
        else if (discovered.Count > 1)
        {
            Update(state => state with
            {
                Preflight = preflight,
                Profile = TopologyProfile.None,
                Ownership = TopologyOwnership.None,
                Topology = null,
                ResourceAuthorityAvailable = false,
                StatusLine = "Conflicting Regular and LoadTests AppHosts detected. Stop one before attaching.",
            });
        }
        else if (matches.Count == 1)
        {
            var snapshot = matches[0];
            Update(state => state with
            {
                Preflight = preflight,
                Profile = snapshot.Profile,
                Ownership = TopologyOwnership.None,
                Topology = snapshot,
                ResourceAuthorityAvailable = false,
                StatusLine = $"{KnownTopologyProfiles.DisplayName(snapshot.Profile)} detected. Attach explicitly to enable operations.",
            });
        }
        else if (discovered.Count == 1)
        {
            var snapshot = discovered[0];
            Update(state => state with
            {
                Preflight = preflight,
                Profile = snapshot.Profile,
                Ownership = TopologyOwnership.None,
                Topology = snapshot,
                ResourceAuthorityAvailable = false,
                StatusLine = $"{snapshot.Profile} is present but not attachable — {snapshot.ErrorSummary ?? "partial or unhealthy graph"}.",
            });
        }
        else
        {
            Update(state => state with
            {
                Preflight = preflight,
                StatusLine = preflight.AllPassed
                    ? "No topology active. Preflight is ready; Start or Attach is explicit."
                    : $"Preflight failed — {string.Join(" | ", preflight.Checks.Where(check => !check.Passed).Select(check => check.Remediation))}",
            });
        }
    }

    public void SelectWorkspace(WorkspaceKind workspace) =>
        Update(state => state with { ActiveWorkspace = workspace });

    public void SelectEvidence(long sequence)
    {
        Update(state => state with
        {
            SelectedEvidence = state.Evidence.FirstOrDefault(record => record.Sequence == sequence),
        });
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var state = State;
        if (state.Profile == TopologyProfile.None)
        {
            await InitializeAsync(ct);
            return;
        }

        if (state.ActiveMutation?.Kind is MutationKind.StartTopology
            or MutationKind.StopTopology
            or MutationKind.SwitchTopology
            or MutationKind.ResourceCommand)
        {
            return;
        }

        var refreshContext = CaptureContext(state);
        var observed = await _aspire.GetSnapshotAsync(refreshContext.Profile, ct);
        if (!IsCurrent(refreshContext))
        {
            return;
        }

        if (!observed.IsReachable)
        {
            var discovered = await _aspire.DiscoverAsync(ct);
            if (!IsCurrent(refreshContext))
            {
                return;
            }

            if (discovered.IsReachable
                && discovered.Snapshots.All(snapshot => snapshot.Profile != refreshContext.Profile))
            {
                if (state.Ownership == TopologyOwnership.Owned && _ownedHandle is not null)
                {
                    try
                    {
                        await _processes.ForgetExitedOwnedAsync(_ownedHandle, ct);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
                    {
                        // ForgetExitedOwnedAsync can throw (e.g. an ownership
                        // verification failure or an "aspire ps" timeout).
                        // The refresh must not abort before clearing stale
                        // ownership below -- doing so would leave the console
                        // showing an Owned AppHost that no longer exists.
                        // Self-heal by still clearing local ownership state,
                        // recording the failure as evidence instead of losing it.
                        AddEvidence(
                            Provenance(refreshContext),
                            EvidenceKind.Topology,
                            $"Failed to clear stale {state.Ownership} ownership for {refreshContext.Profile}",
                            "aspire ps --format Json",
                            refreshContext.Profile.ToString(),
                            null,
                            TimeSpan.Zero,
                            ex.Message,
                            false);
                    }

                    _ownedHandle = null;
                }

                var preflight = await _preflight.RunAsync(State.FaultArmingRequested, ct);
                if (!IsCurrent(refreshContext))
                {
                    return;
                }

                _debouncer.Reset();
                await DeleteSessionFaultConfigAsync(refreshContext.Profile, state.FaultsArmed, CancellationToken.None);
                AddEvidence(
                    Provenance(refreshContext),
                    EvidenceKind.Topology,
                    $"{state.Ownership} AppHost disappeared",
                    "aspire ps --format Json",
                    refreshContext.Profile.ToString(),
                    null,
                    TimeSpan.Zero,
                    "Aspire discovery confirmed the previously active AppHost is no longer running.",
                    false);
                Update(current => current with
                {
                    Profile = TopologyProfile.None,
                    Ownership = TopologyOwnership.None,
                    Topology = null,
                    Preflight = preflight,
                    ResourceAuthorityAvailable = false,
                    LastPayment = null,
                    CanResendLastPayment = false,
                    LastLoadResult = null,
                    LoadProgress = new LoadWorkflowProgress(LoadWorkflowPhase.NotStarted, TimeSpan.Zero, "Not run this session."),
                    FaultsArmed = false,
                    AppliedFaults = null,
                    StagedFaults = null,
                    FaultsAppliedAt = null,
                    FaultsObserved = false,
                    FaultDetail = string.Empty,
                    StatusLine = $"The {state.Ownership} AppHost is no longer running. Start or attach a known topology.",
                });
                return;
            }
        }

        var snapshot = state.Topology is null
            ? observed
            : _debouncer.Observe(state.Topology, observed);

        Update(current => current with
        {
            Topology = snapshot,
            ResourceAuthorityAvailable = current.Ownership != TopologyOwnership.None
                && snapshot.IsReachable
                && snapshot.IsFingerprintMatch
                && snapshot.ErrorSummary is null
                && _time.GetUtcNow() - snapshot.CapturedAt <= _options.SnapshotFreshness,
            StatusLine = snapshot.IsReachable
                ? $"{KnownTopologyProfiles.DisplayName(snapshot.Profile)} · {current.Ownership} · generation {current.RunGeneration}"
                : $"{KnownTopologyProfiles.DisplayName(snapshot.Profile)} · Unreachable — {snapshot.ErrorSummary}",
        });
    }

    public async Task<CommandResult> StartAsync(TopologyProfile profile, CancellationToken ct)
    {
        if (profile == TopologyProfile.None)
        {
            return CommandResult.Rejected("Select Regular or LoadTests.");
        }

        if (State.Profile != TopologyProfile.None)
        {
            return CommandResult.Rejected("A topology is already detected or active. Attach it or stop it before starting another.");
        }

        var preflight = await _preflight.RunAsync(State.FaultArmingRequested, ct);
        Update(state => state with { Preflight = preflight });
        if (!preflight.CanStart(profile))
        {
            return CommandResult.Rejected(
                $"Start blocked by preflight: {string.Join(" | ", preflight.Checks.Where(check => !check.Passed).Select(check => check.Remediation))}");
        }

        if (!TryBeginMutation(MutationKind.StartTopology, KnownTopologyProfiles.DisplayName(profile), out var mutation))
        {
            return BusyResult();
        }

        TopologyHandle? handle = null;
        var armFaults = State.FaultArmingRequested;
        try
        {
            if (armFaults)
            {
                await ResetSessionFaultConfigAsync(profile, ct);
            }

            handle = await _processes.StartOwnedAsync(profile, armFaults, ct);
            _ownedHandle = handle;
            SetTopologyTransition(profile, TopologyOwnership.Owned, "Starting AppHost");
            var snapshot = await WaitForTopologyAsync(profile, expectPresent: true, ct);
            if (snapshot is null)
            {
                var detail = JournalRedaction.Apply(_processes.GetRecentOutput(handle));
                await _processes.StopOwnedAsync(handle, CancellationToken.None);
                _ownedHandle = null;
                AddEvidence(EvidenceKind.Topology, $"Start {profile} timed out", $"aspire start --apphost {handle.ProjectPath}", profile.ToString(), null, TimeSpanSince(mutation.StartedAt), detail, false);
                return CommandResult.Rejected($"Timed out waiting for {profile}. {detail}");
            }

            ActivateTopology(snapshot, TopologyOwnership.Owned);
            await AdoptFaultStateAsync(profile, armFaults, ct);
            AddEvidence(EvidenceKind.Topology, $"Started {profile} as Owned", $"aspire start --apphost {handle.ProjectPath}", profile.ToString(), null, TimeSpanSince(mutation.StartedAt), snapshot.Fingerprint, true);
            return CommandResult.Ok($"{profile} started and verified.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            var error = ex.Message;
            if (handle is not null && _ownedHandle is not null)
            {
                try
                {
                    await _processes.StopOwnedAsync(handle, CancellationToken.None);
                    _ownedHandle = null;
                }
                catch (Exception cleanupError)
                {
                    error = $"{error} Cleanup failed: {cleanupError.Message}";
                }
            }

            AddEvidence(EvidenceKind.Topology, $"Start {profile} failed", handle is null ? "aspire start" : $"aspire start --apphost {handle.ProjectPath}", profile.ToString(), null, TimeSpanSince(mutation.StartedAt), error, false);
            return CommandResult.Rejected(error);
        }
        finally
        {
            EndMutation();
        }
    }

    public async Task<CommandResult> AttachAsync(TopologyProfile profile, CancellationToken ct)
    {
        if (profile == TopologyProfile.None)
        {
            return CommandResult.Rejected("Select Regular or LoadTests.");
        }

        if (State.Ownership != TopologyOwnership.None)
        {
            return CommandResult.Rejected("A topology is already owned or attached.");
        }

        if (!TryBeginMutation(MutationKind.StartTopology, $"Attach {profile}", out var mutation))
        {
            return BusyResult();
        }

        try
        {
            var snapshot = await _aspire.GetSnapshotAsync(profile, ct);
            if (!snapshot.IsReady)
            {
                AddEvidence(EvidenceKind.Topology, $"Attach {profile} rejected", "aspire describe", profile.ToString(), null, TimeSpanSince(mutation.StartedAt), snapshot.ErrorSummary ?? "Fingerprint mismatch.", false);
                return CommandResult.Rejected(snapshot.ErrorSummary ?? "The running graph does not match the known profile.");
            }

            ActivateTopology(snapshot, TopologyOwnership.Attached);
            await AdoptFaultStateAsync(profile, armed: false, ct);
            AddEvidence(EvidenceKind.Topology, $"Attached to {profile}", "aspire describe", profile.ToString(), null, TimeSpanSince(mutation.StartedAt), snapshot.Fingerprint, true);
            return CommandResult.Ok($"{profile} attached as unowned.");
        }
        finally
        {
            EndMutation();
        }
    }

    public async Task<CommandResult> StopAsync(CancellationToken ct)
    {
        var state = State;
        var ownedHandle = _ownedHandle;
        if (state.Ownership != TopologyOwnership.Owned || ownedHandle is null)
        {
            return CommandResult.Rejected("Whole-AppHost Stop is available only for an AppHost owned by this session.");
        }

        if (!TryBeginMutation(MutationKind.StopTopology, state.Profile.ToString(), out var mutation))
        {
            return BusyResult();
        }

        try
        {
            OwnedStopResult stop;
            try
            {
                stop = await _processes.StopOwnedAsync(ownedHandle, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                // StopOwnedAsync can throw on an ownership/PID verification
                // failure or an "aspire ps" timeout -- this must surface as a
                // Rejected result the UI can display, like every other
                // failure path in this controller, not propagate uncaught.
                AddEvidence(EvidenceKind.Topology, $"Stop {state.Profile} failed", $"aspire stop --apphost {ownedHandle.ProjectPath}", state.Profile.ToString(), null, TimeSpanSince(mutation.StartedAt), ex.Message, false);
                return CommandResult.Rejected(ex.Message);
            }

            var stoppedProfile = state.Profile;
            await DeleteSessionFaultConfigAsync(stoppedProfile, state.FaultsArmed, CancellationToken.None);
            AddEvidence(EvidenceKind.Topology, $"Stopped {stoppedProfile}", $"aspire stop --apphost {ownedHandle.ProjectPath}", stoppedProfile.ToString(), null, TimeSpanSince(mutation.StartedAt), stop.Detail, true);
            _ownedHandle = null;
            _debouncer.Reset();
            Update(current => current with
            {
                Profile = TopologyProfile.None,
                Ownership = TopologyOwnership.None,
                Topology = null,
                ResourceAuthorityAvailable = false,
                FaultsArmed = false,
                AppliedFaults = null,
                StagedFaults = null,
                FaultsAppliedAt = null,
                FaultsObserved = false,
                FaultDetail = string.Empty,
                StatusLine = $"{stoppedProfile} stopped. No topology active.",
            });
            return CommandResult.Ok($"{stoppedProfile} stopped.");
        }
        finally
        {
            EndMutation();
        }
    }

    public async Task<CommandResult> SwitchAsync(TopologyProfile target, CancellationToken ct)
    {
        var state = State;
        if (state.Ownership == TopologyOwnership.Attached)
        {
            return CommandResult.Rejected("Switch is disabled for an attached AppHost.");
        }

        if (target == TopologyProfile.None || target == state.Profile)
        {
            return CommandResult.Rejected("Choose the other known topology.");
        }

        var preflight = await _preflight.RunAsync(State.FaultArmingRequested, ct);
        Update(current => current with { Preflight = preflight });
        if (!preflight.DiscoveryReachable)
        {
            return CommandResult.Rejected("Switch blocked because Aspire discovery is Unreachable.");
        }

        var targetState = preflight.Profiles[target];
        if (targetState.Snapshot is not null || !targetState.PortsFree)
        {
            return CommandResult.Rejected($"Switch blocked before stopping the owned AppHost: {targetState.Detail}.");
        }

        if (!TryBeginMutation(MutationKind.SwitchTopology, target.ToString(), out var mutation))
        {
            return BusyResult();
        }

        TopologyHandle? targetHandle = null;
        var armTargetFaults = State.FaultArmingRequested;
        try
        {
            if (_ownedHandle is not null)
            {
                var outgoingProfile = state.Profile;
                var outgoingArmed = state.FaultsArmed;
                await _processes.StopOwnedAsync(_ownedHandle, ct);
                _ownedHandle = null;
                await DeleteSessionFaultConfigAsync(outgoingProfile, outgoingArmed, CancellationToken.None);
                Update(current => current with
                {
                    Profile = TopologyProfile.None,
                    Ownership = TopologyOwnership.None,
                    Topology = null,
                    ResourceAuthorityAvailable = false,
                    LastPayment = null,
                    CanResendLastPayment = false,
                    LastLoadResult = null,
                    LoadProgress = new LoadWorkflowProgress(LoadWorkflowPhase.NotStarted, TimeSpan.Zero, "Not run for the new generation."),
                    FaultsArmed = false,
                    AppliedFaults = null,
                    StagedFaults = null,
                    FaultsAppliedAt = null,
                    FaultsObserved = false,
                    FaultDetail = string.Empty,
                });
            }

            if (armTargetFaults)
            {
                await ResetSessionFaultConfigAsync(target, ct);
            }

            targetHandle = await _processes.StartOwnedAsync(target, armTargetFaults, ct);
            _ownedHandle = targetHandle;
            SetTopologyTransition(target, TopologyOwnership.Owned, "Switching AppHost");
            var snapshot = await WaitForTopologyAsync(target, expectPresent: true, ct);
            if (snapshot is null)
            {
                await _processes.StopOwnedAsync(targetHandle, CancellationToken.None);
                _ownedHandle = null;
                AddEvidence(EvidenceKind.Topology, $"Switch to {target} timed out", $"aspire start --apphost {targetHandle.ProjectPath}", target.ToString(), null, TimeSpanSince(mutation.StartedAt), JournalRedaction.Apply(_processes.GetRecentOutput(targetHandle)), false);
                return CommandResult.Rejected($"Timed out switching to {target}.");
            }

            ActivateTopology(snapshot, TopologyOwnership.Owned);
            await AdoptFaultStateAsync(target, armTargetFaults, ct);
            AddEvidence(EvidenceKind.Topology, $"Switched to {target}", $"aspire start --apphost {targetHandle.ProjectPath}", target.ToString(), null, TimeSpanSince(mutation.StartedAt), snapshot.Fingerprint, true);
            return CommandResult.Ok($"Switched to {target}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            var error = ex.Message;
            if (targetHandle is not null && _ownedHandle is not null)
            {
                try
                {
                    await _processes.StopOwnedAsync(targetHandle, CancellationToken.None);
                    _ownedHandle = null;
                }
                catch (Exception cleanupError)
                {
                    error = $"{error} Cleanup failed: {cleanupError.Message}";
                }
            }

            AddEvidence(EvidenceKind.Topology, $"Switch to {target} failed", "aspire stop + aspire start", target.ToString(), null, TimeSpanSince(mutation.StartedAt), error, false);
            return CommandResult.Rejected(error);
        }
        finally
        {
            EndMutation();
        }
    }

    public async Task<CommandResult> ExecuteResourceCommandAsync(
        string resourceName,
        ResourceCommand command,
        CancellationToken ct)
    {
        var state = State;
        if (!KnownResources.ResourceCommandAllowList.Contains(resourceName))
        {
            return CommandResult.Rejected($"Resource '{resourceName}' is not allow-listed.");
        }

        if (!HasFreshResourceAuthority(state))
        {
            return CommandResult.Rejected("Resource commands require a fresh fingerprint-matching Aspire snapshot.");
        }

        if (!TryBeginMutation(MutationKind.ResourceCommand, $"{command} {resourceName}", out var mutation))
        {
            return BusyResult();
        }

        try
        {
            MarkResourceTransition(resourceName, command);
            var dispatch = await _aspire.ExecuteResourceCommandAsync(state.Profile, resourceName, command, ct);
            if (dispatch.Status == ResourceDispatchStatus.Rejected)
            {
                AddEvidence(EvidenceKind.Resource, $"{command} {resourceName} rejected", $"aspire resource {resourceName} {command.ToString().ToLowerInvariant()}", resourceName, null, TimeSpanSince(mutation.StartedAt), dispatch.Detail, false);
                return CommandResult.Rejected(dispatch.Detail);
            }

            if (dispatch.Status is ResourceDispatchStatus.Ambiguous or ResourceDispatchStatus.Partial)
            {
                var reconciled = await _aspire.GetSnapshotAsync(state.Profile, CancellationToken.None);
                Update(current => current with
                {
                    Topology = reconciled,
                    ResourceAuthorityAvailable = false,
                    StatusLine = dispatch.Status == ResourceDispatchStatus.Partial
                        ? $"Partial mutation — refresh required. {dispatch.Detail}"
                        : $"Ambiguous mutation — refresh required. {dispatch.Detail}",
                });
                AddEvidence(
                    EvidenceKind.Resource,
                    dispatch.Status == ResourceDispatchStatus.Partial
                        ? $"{command} {resourceName} partially applied"
                        : $"{command} {resourceName} ambiguous",
                    ExactResourceCommands(command, dispatch.AffectedInstances, dispatch.FailedInstances),
                    resourceName,
                    null,
                    TimeSpanSince(mutation.StartedAt),
                    dispatch.Detail,
                    false);
                return CommandResult.Rejected(
                    dispatch.Status == ResourceDispatchStatus.Partial
                        ? $"Partial mutation; real topology was reconciled and refresh is required. {dispatch.Detail}"
                        : $"Ambiguous after dispatch; refresh is required before further mutation. {dispatch.Detail}");
            }

            var wait = await WaitForResourceAsync(state.Profile, resourceName, command, ct);
            if (!wait.Confirmed)
            {
                if (wait.Snapshot is not null)
                {
                    Update(current => current with { Topology = wait.Snapshot });
                }
                Update(current => current with { ResourceAuthorityAvailable = false });
                var summary = wait.Partial
                    ? $"{command} {resourceName} partially resolved"
                    : $"{command} {resourceName} ambiguous";
                AddEvidence(EvidenceKind.Resource, summary, ExactResourceCommands(command, dispatch.AffectedInstances, []), resourceName, null, TimeSpanSince(mutation.StartedAt), wait.Detail, false);
                return CommandResult.Rejected(
                    wait.Partial
                        ? $"Partial mutation: {wait.Detail} Refresh is required before further mutation."
                        : $"Ambiguous after dispatch: {wait.Detail} Refresh is required before further mutation.");
            }

            Update(current => current with { Topology = wait.Snapshot });
            AddEvidence(EvidenceKind.Resource, $"{command} {resourceName} confirmed", ExactResourceCommands(command, dispatch.AffectedInstances, []), resourceName, null, TimeSpanSince(mutation.StartedAt), dispatch.Detail, true);
            return CommandResult.Ok($"{resourceName}: {command} confirmed by Aspire.");
        }
        finally
        {
            EndMutation();
        }
    }

    public async Task<PaymentResult> SubmitPaymentAsync(
        PaymentRequest request,
        IdempotencyMode idempotencyMode,
        string? suppliedKey,
        CancellationToken ct)
    {
        var key = idempotencyMode switch
        {
            IdempotencyMode.Generated => _keyFactory(),
            IdempotencyMode.Supplied => suppliedKey,
            _ => null,
        };

        var submission = new PaymentSubmission(request, idempotencyMode, key);
        return await SubmitPaymentInternalAsync(submission, isResend: false, ct);
    }

    public async Task<PaymentResult> ResendLastPaymentAsync(CancellationToken ct)
    {
        var state = State;
        if (!state.CanResendLastPayment || state.LastPayment is null)
        {
            return RejectedPayment("No retry-safe generated or supplied key is available.");
        }

        return await SubmitPaymentInternalAsync(state.LastPayment, isResend: true, ct);
    }

    public async Task<InspectionResult> QueryOutcomeAsync(string transactionIdOrKey, CancellationToken ct)
    {
        var state = State;
        if (state.Profile == TopologyProfile.None || state.Ownership == TopologyOwnership.None)
        {
            return new InspectionResult(false, 0, "outcome", null, "Start or attach a topology before querying an outcome.", TimeSpan.Zero);
        }

        if (string.IsNullOrWhiteSpace(transactionIdOrKey))
        {
            return new InspectionResult(false, 0, "outcome", null, "Enter a transaction id or idempotency key.", TimeSpan.Zero);
        }

        var startedAt = _time.GetUtcNow();
        var context = CaptureContext(state);
        var provenance = Provenance(context);
        var result = await _payments.QueryOutcomeAsync(context.Profile, transactionIdOrKey.Trim(), ct);
        AddEvidence(
            provenance,
            EvidenceKind.OutcomeQuery,
            result.Succeeded ? $"Outcome query returned HTTP {result.StatusCode}" : "Outcome query failed",
            "GET",
            result.Target,
            result.StatusCode,
            result.Duration,
            result.Body ?? result.ErrorSummary ?? string.Empty,
            result.Succeeded);
        return result with { Duration = result.Duration == TimeSpan.Zero ? TimeSpanSince(startedAt) : result.Duration };
    }

    public async Task<InspectionResult> InspectAsync(string endpointId, CancellationToken ct)
    {
        var state = State;
        if (state.Profile == TopologyProfile.None || state.Ownership == TopologyOwnership.None)
        {
            return new InspectionResult(false, 0, endpointId, null, "Start or attach a topology before inspecting evidence.", TimeSpan.Zero);
        }

        var context = CaptureContext(state);
        var result = await _payments.InspectAsync(context.Profile, endpointId, ct);
        AddEvidence(
            Provenance(context),
            EvidenceKind.Inspection,
            result.Succeeded ? $"Inspected {endpointId}" : $"Inspection failed: {endpointId}",
            "GET",
            result.Target,
            result.StatusCode,
            result.Duration,
            result.Body ?? result.ErrorSummary ?? string.Empty,
            result.Succeeded);
        return result;
    }

    public async Task<CommandResult> RunBurstAsync(
        PaymentRequest template,
        int count,
        int concurrency,
        CancellationToken ct)
    {
        if (count < _options.MinimumBurstCount || count > _options.MaximumBurstCount)
        {
            return CommandResult.Rejected($"Burst count must be between {_options.MinimumBurstCount} and {_options.MaximumBurstCount}.");
        }

        if (concurrency < _options.MinimumBurstConcurrency || concurrency > _options.MaximumBurstConcurrency)
        {
            return CommandResult.Rejected($"Burst concurrency must be between {_options.MinimumBurstConcurrency} and {_options.MaximumBurstConcurrency}.");
        }

        if (State.Profile == TopologyProfile.None || State.Ownership == TopologyOwnership.None)
        {
            return CommandResult.Rejected("Start or attach a topology before running a burst.");
        }

        var validation = PaymentInputValidator.Validate(new PaymentSubmission(template, IdempotencyMode.Generated, "burst-validation"));
        if (validation.Count > 0)
        {
            return CommandResult.Rejected(string.Join(" ", validation));
        }

        if (!TryBeginMutation(MutationKind.PaymentBurst, $"{count} payments", out var mutation))
        {
            return BusyResult();
        }

        using var burstCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var context = CaptureContext(State);
        var provenance = Provenance(context);
        _burstCancellation = burstCts;
        var burstNumber = Interlocked.Increment(ref _burstSequence);
        Update(state => state with { Burst = new BurstProgress(count, 0, 0, 0, 0, false) });

        var accepted = 0;
        var completed = 0;
        var failed = 0;
        var sent = 0;
        var failures = new ConcurrentQueue<string>();

        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, count),
                new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = burstCts.Token },
                async (index, token) =>
                {
                    var key = $"demo-burst-{_sessionId}-g{context.RunGeneration:D3}-r{burstNumber:D3}-{index:D6}";
                    PaymentResult result;
                    try
                    {
                        result = EnforceRailSemantics(
                            template.Rail,
                            await _payments.SubmitAsync(
                                context.Profile,
                                new PaymentSubmission(template, IdempotencyMode.Generated, key),
                                token));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        result = RejectedPayment(ex.Message);
                    }

                    Interlocked.Increment(ref sent);
                    switch (result.Outcome)
                    {
                        case PaymentOutcome.Pending:
                            Interlocked.Increment(ref accepted);
                            break;
                        case PaymentOutcome.Completed:
                            Interlocked.Increment(ref completed);
                            break;
                        default:
                            Interlocked.Increment(ref failed);
                            failures.Enqueue($"{key}: {result.ErrorSummary ?? result.ResponseStatus ?? result.Outcome.ToString()}");
                            break;
                    }

                    Update(state => IsCurrent(context)
                        ? state with { Burst = new BurstProgress(count, sent, accepted, completed, failed, false) }
                        : state);
                });
        }
        catch (OperationCanceledException) when (burstCts.IsCancellationRequested)
        {
            Update(state => state with
            {
                Burst = state.Burst with { Cancelled = true },
            });
        }
        finally
        {
            _burstCancellation = null;
            var final = State.Burst;
            var summary = final.Cancelled
                ? $"Burst cancelled after {final.Sent}/{count}; accepted {final.Accepted}, completed {final.Completed}, failed {final.Failed}."
                : $"Burst finished {final.Sent}/{count}; accepted {final.Accepted}, completed {final.Completed}, failed {final.Failed}.";
            AddEvidence(provenance, EvidenceKind.Burst, summary, "POST", KnownEndpoints.PaymentsSubmit, null, TimeSpanSince(mutation.StartedAt), string.Join(Environment.NewLine, failures), !final.Cancelled && final.Failed == 0);
            EndMutation();
        }

        var outcome = State.Burst;
        return outcome.Cancelled
            ? CommandResult.Rejected($"Burst cancelled after {outcome.Sent}/{outcome.Requested}; partial evidence preserved.")
            : outcome.Failed == 0
                ? CommandResult.Ok($"Burst completed: {outcome.Sent}/{outcome.Requested}.")
                : CommandResult.Rejected($"Burst completed with {outcome.Failed} failed requests.");
    }

    public bool CancelActiveBurst()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_state.ActiveMutation?.Kind != MutationKind.PaymentBurst || _burstCancellation is null)
            {
                return false;
            }

            cancellation = _burstCancellation;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sets whether the *next* AppHost start brings up a Dev Proxy.
    /// <c>Features:UseDevProxy</c> is read when the AppHost starts, so this can never be a
    /// live on/off switch: on a running topology it is read-only, and on an Attached one it
    /// is refused outright because this session did not start it.
    /// </summary>
    public CommandResult SetArming(bool armed)
    {
        var state = State;
        if (state.Ownership == TopologyOwnership.Attached)
        {
            return CommandResult.Rejected(
                "Attached — this AppHost is not owned by this session, so its arming cannot be changed.");
        }

        if (state.Ownership == TopologyOwnership.Owned)
        {
            return CommandResult.Rejected(
                "Arming is read when the AppHost starts. Stop this AppHost and start it again to change it.");
        }

        Update(current => current with { FaultArmingRequested = armed });
        return CommandResult.Ok(armed
            ? "Faults armed on next AppHost start."
            : "Faults not armed on next AppHost start.");
    }

    /// <summary>
    /// Stages every knob without touching the running system. Staging is deliberately inert:
    /// escalation is two-step, so a stray keypress can never make the system worse.
    /// </summary>
    public CommandResult StageFaults(FaultLevels levels)
    {
        var state = State;
        if (!state.FaultsArmed)
        {
            return CommandResult.Rejected(FaultsUnavailableReason(state));
        }

        var staged = levels.Normalized();
        Update(current => current with { StagedFaults = staged });
        return CommandResult.Ok($"Staged {staged}. Nothing is applied until Apply fires.");
    }

    /// <summary>
    /// Commits every staged knob in one config write, so the running system never observes
    /// a half-applied combination.
    /// <para>
    /// Deliberately does <b>not</b> call <see cref="TryBeginMutation"/> — the fault controls
    /// are the console's second named exemption from the single-action-in-flight lock, after
    /// burst Cancel. Raising latency <i>while</i> a burst is in flight is the demonstration
    /// itself, and it is safe because this mutates proxy configuration, never banking state.
    /// </para>
    /// </summary>
    public Task<CommandResult> ApplyFaultsAsync(CancellationToken ct)
    {
        var state = State;
        if (!state.FaultsArmed)
        {
            return Task.FromResult(CommandResult.Rejected(FaultsUnavailableReason(state)));
        }

        if (!state.HasStagedFaultChange)
        {
            return Task.FromResult(CommandResult.Rejected(
                "Nothing is staged — move a slider or pick a preset before applying."));
        }

        return CommitFaultsAsync(state.Staged, "Apply faults", retainStagedOnFailure: true, ct);
    }

    /// <summary>
    /// Sets every knob to zero and applies immediately, in one step, with no confirmation and
    /// no staging — de-escalation must never be gated behind the thing currently going wrong.
    /// Lock-exempt for the same reason <see cref="ApplyFaultsAsync"/> is.
    /// </summary>
    public Task<CommandResult> PanicOffAsync(CancellationToken ct)
    {
        var state = State;
        if (!state.FaultsArmed)
        {
            return Task.FromResult(CommandResult.Rejected(FaultsUnavailableReason(state)));
        }

        return CommitFaultsAsync(FaultLevels.AllZero, "Panic-off", retainStagedOnFailure: false, ct);
    }

    public static string FaultsUnavailableReason(OperatorConsoleState state) => state switch
    {
        { Ownership: TopologyOwnership.Attached } =>
            "Attached — this session did not start this AppHost and cannot arm its Dev Proxy. "
            + "Stop it outside this console, then Start it here with faults armed.",
        { Ownership: TopologyOwnership.None } =>
            "No topology active — go to Resources (2) and Start one with faults armed.",
        _ => "This topology started without a Dev Proxy. Stop it and start it again with faults armed.",
    };

    /// <summary>
    /// The one place a fault level is committed. Serialized by <see cref="_faultCommitGate"/>
    /// because Apply and panic-off are both lock-exempt and <c>0</c> is bound window-wide:
    /// without it two commits can interleave and leave the reported levels disagreeing with
    /// what is actually in the file.
    /// </summary>
    private async Task<CommandResult> CommitFaultsAsync(
        FaultLevels levels,
        string action,
        bool retainStagedOnFailure,
        CancellationToken ct)
    {
        await _faultCommitGate.WaitAsync(ct);
        try
        {
            return await CommitFaultsCoreAsync(levels, action, retainStagedOnFailure, ct);
        }
        finally
        {
            _faultCommitGate.Release();
        }
    }

    private async Task<CommandResult> CommitFaultsCoreAsync(
        FaultLevels levels,
        string action,
        bool retainStagedOnFailure,
        CancellationToken ct)
    {
        var context = CaptureContext(State);
        var startedAt = _time.GetUtcNow();
        var applied = levels.Normalized();
        var result = await _faults.WriteAsync(context.Profile, applied, ct);
        if (!IsCurrent(context))
        {
            // The topology was stopped or switched under this write. The file belongs to a
            // profile/generation that is no longer active, so nothing may be stamped onto the
            // current one -- the same discipline every other awaiting path here follows.
            AddEvidence(
                Provenance(context),
                EvidenceKind.Fault,
                $"{action} discarded — the topology changed while the config was being written",
                "WRITE",
                result.Path,
                null,
                TimeSpanSince(startedAt),
                $"Captured for {context.Profile} generation {context.RunGeneration}; "
                + $"now {State.Profile} generation {State.RunGeneration}.",
                false);
            return CommandResult.Rejected(
                "The topology changed while the fault config was being written; no level was applied to it.");
        }

        if (!result.Succeeded)
        {
            var error = result.ErrorSummary ?? "The generated Dev Proxy session config could not be written.";
            Update(current => current with
            {
                // Applied levels and the chip are both left untouched: nothing reached the
                // proxy, so claiming otherwise would be the exact lie this console avoids.
                StagedFaults = retainStagedOnFailure ? current.StagedFaults : current.Applied,
                FaultDetail = error,
            });
            AddEvidence(
                Provenance(context),
                EvidenceKind.Fault,
                $"{action} failed — no level reached the proxy",
                "WRITE",
                result.Path,
                null,
                TimeSpanSince(startedAt),
                error,
                false);
            return CommandResult.Rejected(error);
        }

        Update(current => current with
        {
            AppliedFaults = applied,
            StagedFaults = applied,
            FaultsAppliedAt = _time.GetUtcNow(),
            // All-zero lands on Armed immediately: there is no traffic effect to observe.
            // Anything else stays "Applied — not yet observed in traffic" until proof arrives.
            FaultsObserved = applied.IsAllZero,
            FaultDetail = string.Empty,
        });
        AddEvidence(
            new EvidenceProvenance(context.Profile, context.RunGeneration, applied.IsAllZero ? null : applied),
            EvidenceKind.Fault,
            applied.IsAllZero
                ? $"{action} — every knob at zero, nothing is being injected"
                : $"{action} — {applied}; applied, not yet observed in traffic",
            "WRITE",
            result.Path,
            null,
            TimeSpanSince(startedAt),
            applied.ToString(),
            true);
        return CommandResult.Ok(applied.IsAllZero
            ? "Every fault knob is at zero."
            : $"Applied {applied}. Waiting for traffic to carry it.");
    }

    /// <summary>
    /// Rewrites the generated session config quiet before an armed start, so a config left
    /// behind by a prior session can never silently apply its levels to this one. A failure
    /// here is recorded but never blocks the start: the AppHost simply falls back to its
    /// checked-in profile, which the console then reads and reports honestly.
    /// </summary>
    private async Task ResetSessionFaultConfigAsync(TopologyProfile profile, CancellationToken ct)
    {
        var reset = await _faults.ResetAsync(profile, ct);
        if (!reset.Succeeded)
        {
            AddEvidence(
                EvidenceKind.Fault,
                "Could not reset the generated Dev Proxy session config before arming",
                "WRITE",
                reset.Path,
                null,
                TimeSpan.Zero,
                reset.ErrorSummary ?? string.Empty,
                false);
        }
    }

    /// <summary>
    /// Reads the levels the topology is actually running under after a Start/Attach/Switch —
    /// the generated session config if present, otherwise the checked-in profile, never an
    /// invented zero. Sets the sliders only; it never sets <c>FaultsObserved</c>.
    /// </summary>
    /// <summary>
    /// Removes the generated session config when this session stops owning a topology. Only
    /// ever deletes a file this session armed and therefore wrote — a config another session
    /// left behind is not ours to remove. A surviving file would shadow the checked-in profile
    /// for every later non-console run and silently disable the shipped presets, so a failure
    /// to delete is reported rather than swallowed.
    /// </summary>
    private async Task DeleteSessionFaultConfigAsync(TopologyProfile profile, bool armed, CancellationToken ct)
    {
        if (!armed || profile == TopologyProfile.None)
        {
            return;
        }

        var deleted = await _faults.DeleteAsync(profile, ct);
        if (!deleted.Succeeded)
        {
            AddEvidence(
                EvidenceKind.Fault,
                "Could not remove the generated Dev Proxy session config — it will shadow the checked-in profile",
                "DELETE",
                deleted.Path,
                null,
                TimeSpan.Zero,
                deleted.ErrorSummary ?? string.Empty,
                false);
        }
    }

    private async Task AdoptFaultStateAsync(TopologyProfile profile, bool armed, CancellationToken ct)
    {
        var read = await _faults.ReadAsync(profile, ct);
        // Normalized here rather than trusting adapter discipline: the ladder invariant the
        // sliders depend on is the controller's to hold, not an adapter's to remember.
        var levels = read.Levels.Normalized();
        Update(state => state with
        {
            FaultsArmed = armed,
            AppliedFaults = levels,
            StagedFaults = levels,
            FaultsAppliedAt = null,
            // Reading a config file sets the sliders and nothing else. Observation is proof
            // that traffic carried a level, and no file read is ever that proof.
            FaultsObserved = false,
            FaultLevelsFromSession = read.FromGeneratedSession,
            FaultDetail = read.ErrorSummary ?? string.Empty,
        });

        if (!read.Succeeded)
        {
            AddEvidence(
                EvidenceKind.Fault,
                "Could not read the Dev Proxy levels in force — showing the checked-in defaults",
                "READ",
                read.Path,
                null,
                TimeSpan.Zero,
                read.ErrorSummary ?? string.Empty,
                false);
        }
    }

    public async Task<LoadWorkflowResult> RunLoadTestAsync(int? expectedUniqueCount, CancellationToken ct)
    {
        var state = State;
        if (state.Profile != TopologyProfile.LoadTests || state.Ownership == TopologyOwnership.None)
        {
            return LoadWorkflowResult.Failure(LoadWorkflowPhase.Reset, "Load Test requires an active LoadTests topology.");
        }

        if (!HasFreshResourceAuthority(state) || state.Topology?.IsReady != true)
        {
            return LoadWorkflowResult.Failure(LoadWorkflowPhase.Reset, "Load Test requires a fresh, verified LoadTests snapshot.");
        }

        if (expectedUniqueCount is null or <= 0)
        {
            return LoadWorkflowResult.Failure(LoadWorkflowPhase.Reset, "Expected unique count must be a positive integer.");
        }

        if (!TryBeginMutation(MutationKind.LoadTest, "Reset → Run → Wait → Assert → Investigate", out var mutation))
        {
            return LoadWorkflowResult.Failure(LoadWorkflowPhase.NotStarted, BusyResult().Message);
        }

        var context = CaptureContext(State);
        var provenance = Provenance(context);
        try
        {
            var progress = new InlineProgress<LoadWorkflowProgress>(value =>
                Update(current => IsCurrent(context)
                    ? current with
                    {
                        LoadProgress = value,
                        StatusLine = $"Load Test · {value.Phase} — {value.Detail}",
                    }
                    : current));

            var result = await _loadWorkflow.RunAsync(expectedUniqueCount, progress, ct);
            Update(current => IsCurrent(context) ? current with { LastLoadResult = result } : current);
            AddEvidence(
                provenance,
                EvidenceKind.LoadTest,
                result.AllPassed ? "Load workflow passed" : $"Load workflow did not pass at {result.FinalPhase}",
                "accepted load workflow",
                "Reset → Run → Wait → Assert → Investigate",
                null,
                TimeSpanSince(mutation.StartedAt),
                result.InvestigationDetail + Environment.NewLine + result.ErrorSummary,
                result.AllPassed);
            return result;
        }
        finally
        {
            EndMutation();
        }
    }

    public async Task<EvidenceExportResult> ExportEvidenceAsync(CancellationToken ct)
    {
        var state = State;
        var result = await _exporter.ExportAsync(state.Evidence, ct);
        AddEvidence(
            Provenance(state),
            EvidenceKind.Export,
            result.Succeeded ? "Session evidence exported" : "Evidence export failed",
            "WRITE",
            result.Path,
            null,
            TimeSpan.Zero,
            result.ErrorSummary ?? result.Path,
            result.Succeeded);
        return result;
    }

    public Task<LinkOpenResult> OpenKnownLinkAsync(string linkId, CancellationToken ct)
    {
        if (!KnownLinks.All.Contains(linkId))
        {
            return Task.FromResult(new LinkOpenResult(false, null));
        }

        var resolvedUrl = linkId == KnownLinks.AspireDashboard
            ? State.Topology?.DashboardUrl
            : null;
        return _browser.OpenAsync(linkId, resolvedUrl, ct);
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        Task? activeMutation;
        lock (_sync)
        {
            _shutdownRequested = true;
            activeMutation = _activeMutationCompletion?.Task;
        }

        CancelActiveBurst();
        if (activeMutation is not null)
        {
            await activeMutation.WaitAsync(ct);
        }

        if (_ownedHandle is not null)
        {
            var ownedProfile = _ownedHandle.Profile;
            var armed = State.FaultsArmed;
            await _processes.StopOwnedAsync(_ownedHandle, ct);
            _ownedHandle = null;
            await DeleteSessionFaultConfigAsync(ownedProfile, armed, ct);
        }
    }

    private async Task<PaymentResult> SubmitPaymentInternalAsync(
        PaymentSubmission submission,
        bool isResend,
        CancellationToken ct)
    {
        var validation = PaymentInputValidator.Validate(submission);
        if (validation.Count > 0)
        {
            return RejectedPayment(string.Join(" ", validation));
        }

        var state = State;
        if (state.Profile == TopologyProfile.None || state.Ownership == TopologyOwnership.None)
        {
            return RejectedPayment("Start or attach a topology before submitting a payment.");
        }

        if (!TryBeginMutation(MutationKind.SubmitPayment, isResend ? "Resend payment" : "Submit payment", out var mutation))
        {
            return RejectedPayment(BusyResult().Message);
        }

        var context = CaptureContext(State);
        var provenance = Provenance(context);
        try
        {
            var result = await _payments.SubmitAsync(context.Profile, submission, ct);
            var safeResult = EnforceRailSemantics(submission.Request.Rail, result);
            var canResend = submission.IdempotencyMode != IdempotencyMode.Omitted;
            if (submission.IdempotencyMode == IdempotencyMode.Omitted && safeResult.IsAmbiguous)
            {
                canResend = false;
            }

            Update(state => IsCurrent(context) ? state with
            {
                LastPayment = submission,
                CanResendLastPayment = canResend,
            } : state);

            var summary = safeResult.Outcome switch
            {
                PaymentOutcome.Pending => $"{safeResult.StatusCode} Pending — no committed outcome yet",
                PaymentOutcome.Ambiguous => "Ambiguous — not yet reconciled; Resend is unsafe",
                PaymentOutcome.Completed => $"{safeResult.StatusCode} Completed",
                PaymentOutcome.Failed => $"{safeResult.StatusCode} Failed",
                _ => safeResult.ErrorSummary ?? safeResult.Outcome.ToString(),
            };
            AddEvidence(
                provenance,
                EvidenceKind.Payment,
                isResend ? $"Resend same key · {summary}" : summary,
                "POST",
                KnownEndpoints.PaymentsSubmit,
                safeResult.StatusCode,
                safeResult.Duration == TimeSpan.Zero ? TimeSpanSince(mutation.StartedAt) : safeResult.Duration,
                $"Idempotency {submission.IdempotencyMode}: {submission.IdempotencyKey ?? "(omitted)"}{Environment.NewLine}"
                + (safeResult.Body ?? safeResult.ErrorSummary ?? string.Empty),
                safeResult.Outcome is PaymentOutcome.Pending or PaymentOutcome.Completed or PaymentOutcome.Failed);
            return safeResult;
        }
        finally
        {
            EndMutation();
        }
    }

    private PaymentResult EnforceRailSemantics(PaymentRail rail, PaymentResult result)
    {
        if (result.IsAmbiguous)
        {
            return result;
        }

        if (rail == PaymentRail.Standard && result.StatusCode != 202)
        {
            return result with
            {
                Outcome = PaymentOutcome.TransportFailure,
                ErrorSummary = $"Standard payments must return 202 Pending, not HTTP {result.StatusCode}.",
            };
        }

        if (rail == PaymentRail.Instant
            && result.StatusCode != 202
            && result.StatusCode != 200)
        {
            return result with
            {
                Outcome = PaymentOutcome.TransportFailure,
                ErrorSummary = $"Instant payments must return committed 200 or durable 202, not HTTP {result.StatusCode}.",
            };
        }

        return result;
    }

    private bool HasFreshResourceAuthority(OperatorConsoleState state) =>
        state.Profile != TopologyProfile.None
        && state.Ownership != TopologyOwnership.None
        && state.ResourceAuthorityAvailable
        && state.Topology is { IsReachable: true, IsFingerprintMatch: true } snapshot
        && snapshot.ErrorSummary is null
        && _time.GetUtcNow() - snapshot.CapturedAt <= _options.SnapshotFreshness;

    private async Task<TopologySnapshot?> WaitForTopologyAsync(
        TopologyProfile profile,
        bool expectPresent,
        CancellationToken ct)
    {
        var startedAt = _time.GetUtcNow();
        var deadline = _time.GetUtcNow() + _options.TransitionTimeout;
        for (var attempt = 0; attempt < 240 && _time.GetUtcNow() <= deadline; attempt++)
        {
            var snapshot = await _aspire.GetSnapshotAsync(profile, ct);
            if (expectPresent && snapshot.IsReady)
            {
                return snapshot;
            }

            if (!expectPresent && !snapshot.IsReachable)
            {
                return snapshot;
            }

            Update(state => state with
            {
                StatusLine = $"{profile} · transition Running — {(_time.GetUtcNow() - startedAt).TotalSeconds:F0}s",
            });
            await DelayAsync(ct);
        }

        return null;
    }

    private async Task<ResourceWaitResult> WaitForResourceAsync(
        TopologyProfile profile,
        string resourceName,
        ResourceCommand command,
        CancellationToken ct)
    {
        var startedAt = _time.GetUtcNow();
        var deadline = _time.GetUtcNow() + _options.TransitionTimeout;
        TopologySnapshot? lastSnapshot = null;
        for (var attempt = 0; attempt < 240 && _time.GetUtcNow() <= deadline; attempt++)
        {
            var snapshot = await _aspire.GetSnapshotAsync(profile, ct);
            lastSnapshot = snapshot;
            var resource = snapshot.FindResource(resourceName);
            if (snapshot.IsReachable && snapshot.IsFingerprintMatch && ResourceReachedTarget(resource, command))
            {
                return new ResourceWaitResult(true, false, snapshot, "Aspire confirmed the requested state.");
            }

            if (resource?.Condition is ResourceCondition.Degraded or ResourceCondition.Failed)
            {
                return new ResourceWaitResult(
                    false,
                    true,
                    snapshot,
                    $"Aspire reports {resource.Condition} after dispatch: {resource.Detail}");
            }

            var elapsed = _time.GetUtcNow() - startedAt;
            Update(state =>
            {
                var resources = snapshot.Resources
                    .Select(item => string.Equals(item.Name, resourceName, StringComparison.Ordinal)
                        ? item with
                        {
                            Condition = ResourceCondition.Running,
                            Detail = $"{command} dispatched — {elapsed.TotalSeconds:F0}s",
                        }
                        : item)
                    .ToList();
                return state with
                {
                    Topology = snapshot with { Resources = resources },
                    StatusLine = $"{command} {resourceName} · Running — {elapsed.TotalSeconds:F0}s",
                };
            });
            await DelayAsync(ct);
        }

        return new ResourceWaitResult(
            false,
            false,
            lastSnapshot,
            "Aspire did not confirm the requested terminal state before timeout.");
    }

    private static bool ResourceReachedTarget(ResourceSnapshot? resource, ResourceCommand command) =>
        resource is not null
        && command switch
        {
            ResourceCommand.Stop => resource.Condition == ResourceCondition.Stopped,
            ResourceCommand.Start or ResourceCommand.Restart =>
                resource.Condition is ResourceCondition.Healthy or ResourceCondition.Running or ResourceCondition.Completed,
            _ => false,
        };

    private Task DelayAsync(CancellationToken ct) =>
        _options.PollInterval <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(_options.PollInterval, _time, ct);

    private void ActivateTopology(TopologySnapshot snapshot, TopologyOwnership ownership)
    {
        _debouncer.Reset();
        Update(state => state with
        {
            Profile = snapshot.Profile,
            Ownership = ownership,
            RunGeneration = state.RunGeneration + 1,
            Topology = snapshot,
            ResourceAuthorityAvailable = true,
            LastPayment = null,
            CanResendLastPayment = false,
            LastLoadResult = null,
            LoadProgress = new LoadWorkflowProgress(LoadWorkflowPhase.NotStarted, TimeSpan.Zero, "Not run for this generation."),
            FaultsArmed = false,
            AppliedFaults = null,
            StagedFaults = null,
            FaultsAppliedAt = null,
            FaultsObserved = false,
            FaultDetail = string.Empty,
            StatusLine = $"{KnownTopologyProfiles.DisplayName(snapshot.Profile)} · {ownership} · generation {state.RunGeneration + 1}",
        });
    }

    private void SetTopologyTransition(TopologyProfile profile, TopologyOwnership ownership, string detail)
    {
        _debouncer.Reset();
        Update(state => state with
        {
            Profile = profile,
            Ownership = ownership,
            Topology = new TopologySnapshot(profile, _time.GetUtcNow(), true, false, string.Empty, [], detail),
            ResourceAuthorityAvailable = false,
            StatusLine = $"{profile} · {detail}",
        });
    }

    private void MarkResourceTransition(string resourceName, ResourceCommand command)
    {
        Update(state =>
        {
            if (state.Topology is null)
            {
                return state;
            }

            var resources = state.Topology.Resources
                .Select(resource => string.Equals(resource.Name, resourceName, StringComparison.Ordinal)
                    ? resource with
                    {
                        Condition = ResourceCondition.Running,
                        Detail = $"{command} dispatched — 0s",
                    }
                    : resource)
                .ToList();
            return state with
            {
                Topology = state.Topology with { Resources = resources },
                StatusLine = $"{state.Profile} · {command} {resourceName} dispatched",
            };
        });
    }

    private bool TryBeginMutation(MutationKind kind, string target, out ActiveMutation mutation)
    {
        lock (_sync)
        {
            if (_shutdownRequested || _state.ActiveMutation is not null)
            {
                mutation = _state.ActiveMutation ?? new ActiveMutation(kind, target, _time.GetUtcNow());
                return false;
            }

            mutation = new ActiveMutation(kind, target, _time.GetUtcNow());
            _activeMutationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _state = _state with
            {
                ActiveMutation = mutation,
                StatusLine = $"{kind}: {target} — Running",
            };
        }

        NotifyStateChanged();
        return true;
    }

    private void EndMutation()
    {
        TaskCompletionSource? completion;
        lock (_sync)
        {
            completion = _activeMutationCompletion;
            _activeMutationCompletion = null;
        }

        Update(state => state with
        {
            ActiveMutation = null,
            StatusLine = state.Evidence.LastOrDefault()?.Summary ?? state.StatusLine,
        });
        completion?.TrySetResult();
    }

    private static CommandResult BusyResult() =>
        CommandResult.Rejected("Another mutating action is already in flight.");

    private static PaymentResult RejectedPayment(string error) =>
        new(PaymentOutcome.Rejected, 0, null, null, null, null, error, TimeSpan.Zero);

    private void AddEvidence(
        EvidenceKind kind,
        string summary,
        string method,
        string target,
        int? statusCode,
        TimeSpan duration,
        string detail,
        bool succeeded)
    {
        var state = State;
        AddEvidence(
            Provenance(state),
            kind,
            summary,
            method,
            target,
            statusCode,
            duration,
            detail,
            succeeded);
    }

    private void AddEvidence(
        EvidenceProvenance provenance,
        EvidenceKind kind,
        string summary,
        string method,
        string target,
        int? statusCode,
        TimeSpan duration,
        string detail,
        bool succeeded)
    {
        Update(state =>
        {
            var records = state.Evidence.ToList();
            var record = new EvidenceRecord(
                Interlocked.Increment(ref _evidenceSequence),
                _time.GetUtcNow(),
                provenance.Profile,
                provenance.RunGeneration,
                kind,
                JournalRedaction.Apply(summary),
                method,
                target,
                statusCode,
                duration,
                JournalRedaction.Apply(detail ?? string.Empty),
                succeeded,
                provenance.Faults);
            records.Add(record);
            if (records.Count > _options.MaximumEvidenceRecords)
            {
                records.RemoveRange(0, records.Count - _options.MaximumEvidenceRecords);
            }

            return state with
            {
                Evidence = records,
                SelectedEvidence = record,
                FaultsObserved = state.FaultsObserved || CarriesAppliedFaults(state, record),
                StatusLine = $"{KnownTopologyProfiles.DisplayName(record.Profile)} · generation {record.RunGeneration} · {record.Summary}",
            };
        });
    }

    /// <summary>
    /// Decides whether one evidence record is proof that the applied levels reached real
    /// traffic. Dev Proxy owns its own reload timing, so a written file is never proof; only
    /// a call that actually carried the levels is.
    /// <para>
    /// Aggregate records (a whole burst, a whole load workflow) are deliberately excluded:
    /// their duration is the duration of the batch, not of an intercepted call, and would
    /// clear any latency floor without proving anything.
    /// </para>
    /// </summary>
    private static bool CarriesAppliedFaults(OperatorConsoleState state, EvidenceRecord record)
    {
        if (!state.FaultsArmed
            || state.FaultsAppliedAt is not { } appliedAt
            || state.Applied.IsAllZero
            || record.Timestamp < appliedAt
            || record.Kind is not (EvidenceKind.Payment or EvidenceKind.OutcomeQuery or EvidenceKind.Inspection))
        {
            return false;
        }

        // A zero floor is never proof, and a duration wildly past the applied ceiling is a
        // real outage rather than the injected band -- see FaultLevels.IsCarriedByDuration.
        var carriesLatency = state.Applied.IsCarriedByDuration(record.Duration);
        var carriesError = state.Applied.InjectsErrors && record.StatusCode is 503 or 429 or 500;
        var carriesThrottling = state.Applied.InjectsThrottling && record.StatusCode is 429;
        return carriesLatency || carriesError || carriesThrottling;
    }

    private TimeSpan TimeSpanSince(DateTimeOffset startedAt) => _time.GetUtcNow() - startedAt;

    private OperationContext CaptureContext(OperatorConsoleState state) =>
        new(state.Profile, state.RunGeneration, state.Topology?.Fingerprint ?? string.Empty, FaultsInForce(state));

    /// <summary>
    /// The levels actually being injected right now, or <c>null</c> when nothing is. An
    /// armed proxy with every knob at zero is not a fault and must never be stamped as one.
    /// </summary>
    private static FaultLevels? FaultsInForce(OperatorConsoleState state) =>
        state.FaultsArmed && !state.Applied.IsAllZero ? state.Applied : null;

    private static EvidenceProvenance Provenance(OperationContext context) =>
        new(context.Profile, context.RunGeneration, context.Faults);

    private static EvidenceProvenance Provenance(OperatorConsoleState state) =>
        new(state.Profile, state.RunGeneration, FaultsInForce(state));

    private bool IsCurrent(OperationContext context)
    {
        var state = State;
        return state.Profile == context.Profile
            && state.RunGeneration == context.RunGeneration
            && string.Equals(state.Topology?.Fingerprint ?? string.Empty, context.Fingerprint, StringComparison.Ordinal);
    }

    private static string ExactResourceCommands(
        ResourceCommand command,
        IReadOnlyList<string> affectedInstances,
        IReadOnlyList<string> failedInstances)
    {
        var instances = affectedInstances.Concat(failedInstances).Distinct(StringComparer.Ordinal).ToList();
        return instances.Count == 0
            ? $"aspire resource <unconfirmed> {command.ToString().ToLowerInvariant()}"
            : string.Join(
                " && ",
                instances.Select(instance => $"aspire resource {instance} {command.ToString().ToLowerInvariant()}"));
    }

    private void Update(Func<OperatorConsoleState, OperatorConsoleState> update)
    {
        lock (_sync)
        {
            _state = update(_state);
        }

        NotifyStateChanged();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke(State);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record ResourceWaitResult(
        bool Confirmed,
        bool Partial,
        TopologySnapshot? Snapshot,
        string Detail);
}
