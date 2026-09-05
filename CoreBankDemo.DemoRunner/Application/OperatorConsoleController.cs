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
    private readonly IOutcomeFeed _outcomeFeed;
    private readonly TimeProvider _time;
    private readonly OperatorConsoleOptions _options;
    private readonly Func<string> _keyFactory;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly object _sync = new();
    private readonly TopologyObservationDebouncer _debouncer = new();
    private readonly SemaphoreSlim _faultCommitGate = new(1, 1);

    /// <summary>
    /// Transaction ids submitted by a burst, mapped to the burst that submitted them. A burst's
    /// outcomes are counted, never followed row by row, so these never become tracked payments
    /// -- but they are still attributed, which is what keeps them out of "Unattributed".
    /// </summary>
    private readonly ConcurrentDictionary<string, BurstTransaction> _burstTransactions = new(StringComparer.Ordinal);

    private OperatorConsoleState _state = OperatorConsoleState.Empty;
    private TopologyHandle? _ownedHandle;
    private CancellationTokenSource? _burstCancellation;
    private TaskCompletionSource? _activeMutationCompletion;
    private bool _shutdownRequested;
    private long _evidenceSequence;
    private long _paymentSequence;
    private int _burstSequence;
    private int _activeBurstNumber;

    /// <summary>
    /// The topology the current subscription belongs to. An event that arrives after a stop or
    /// a switch is discarded against this, through the same staleness guard every other async
    /// completion in this controller uses.
    /// </summary>
    private OperationContext? _feedContext;

    public OperatorConsoleController(
        IAspireAdapter aspire,
        IProcessAdapter processes,
        IPaymentGateway payments,
        ILoadWorkflowRunner loadWorkflow,
        IEvidenceExporter exporter,
        IFaultInjector faults,
        IBrowserLauncher browser,
        IPreflightRunner preflight,
        IOutcomeFeed outcomeFeed,
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
        _outcomeFeed = outcomeFeed;
        _time = time;
        _options = options ?? new OperatorConsoleOptions();
        _keyFactory = keyFactory ?? (() => Guid.NewGuid().ToString("D"));
        _outcomeFeed.EventReceived += OnOutcomeEventReceived;
        _outcomeFeed.StatusChanged += OnFeedStatusChanged;
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
                await StopOutcomeFeedAsync(CancellationToken.None);
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
                    TrackedPayments = [],
                    Feed = OutcomeFeedStatus.NotStarted,
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

        if (FeedReconnectInFlight.IsCompleted)
        {
            // Started, deliberately not awaited: re-establishing waits on a sidecar readiness
            // probe of up to twenty seconds, and blocking the 1.5-second poll on that would
            // freeze the topology chips and the whole UI behind it.
            FeedReconnectInFlight = TryReestablishOutcomeFeedAsync(refreshContext, ct);
        }
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
            await StartOutcomeFeedAsync(profile, ct);
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
            await StartOutcomeFeedAsync(profile, ct);
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
            await StopOutcomeFeedAsync(CancellationToken.None);
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
                TrackedPayments = [],
                Feed = OutcomeFeedStatus.NotStarted,
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
                await StopOutcomeFeedAsync(CancellationToken.None);
                await DeleteSessionFaultConfigAsync(outgoingProfile, outgoingArmed, CancellationToken.None);
                Update(current => current with
                {
                    Profile = TopologyProfile.None,
                    Ownership = TopologyOwnership.None,
                    Topology = null,
                    ResourceAuthorityAvailable = false,
                    LastPayment = null,
                    CanResendLastPayment = false,
                    TrackedPayments = [],
                    Feed = OutcomeFeedStatus.NotStarted,
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
            await StartOutcomeFeedAsync(target, ct);
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
        // The proven leg belongs to this burst alone; a previous burst's ids must never move
        // this one's counters. They are retired rather than forgotten, so a still-in-flight
        // outcome from the previous burst is not announced as a stranger's transaction.
        foreach (var previous in _burstTransactions.Keys)
        {
            _retiredTransactions.Add(previous);
        }

        _burstTransactions.Clear();
        Volatile.Write(ref _activeBurstNumber, burstNumber);
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
                    if (result.TransactionId is { Length: > 0 } burstTransactionId)
                    {
                        // Attributed but never given a row: a burst's outcomes are counted,
                        // not followed (EXPERIENCE.md, Why the outcome feedback loop adds no
                        // surface). Registering the id is what keeps its events out of
                        // "Unattributed".
                        //
                        // Only ids whose HTTP leg was accepted or completed join the proven
                        // leg. An HTTP-failed submission is not part of `Accepted + Completed`,
                        // so counting its broadcast would drive `awaiting` negative and the
                        // clamp would hide a genuine HTTP-versus-broadcast disagreement --
                        // exactly the finding this feature exists to surface. It is retired
                        // instead: labelled as the console's own, counted toward nothing.
                        if (result.Outcome is PaymentOutcome.Pending or PaymentOutcome.Completed)
                        {
                            _burstTransactions[burstTransactionId] = new BurstTransaction(burstNumber);
                            ResolveBufferedBurstEvents(burstTransactionId, burstNumber);
                        }
                        else
                        {
                            _retiredTransactions.Add(burstTransactionId);
                        }
                    }

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

                    // Only the HTTP leg is written here. The proven leg is moved solely by
                    // received events, so a rewrite of the whole record would silently reset it.
                    Update(state => IsCurrent(context)
                        ? state with
                        {
                            Burst = state.Burst with
                            {
                                Requested = count,
                                Sent = sent,
                                Accepted = accepted,
                                Completed = completed,
                                Failed = failed,
                            },
                        }
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
            var summary = (final.Cancelled
                    ? $"Burst cancelled after {final.Sent}/{count}; accepted {final.Accepted}, completed {final.Completed}, failed {final.Failed}."
                    : $"Burst finished {final.Sent}/{count}; accepted {final.Accepted}, completed {final.Completed}, failed {final.Failed}.")
                // The HTTP leg is what the API answered; the proven leg is what the broadcast
                // confirmed, and it keeps moving after this record is written.
                + $" Proven so far: settled {final.Settled}, rejected {final.Rejected}, awaiting {final.Awaiting}.";
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

        // The written file is not enough. Dev Proxy 3.2.0's own restart-on-config-change is
        // broken -- after "Configuration file changed. Restarting proxy..." it accepts TCP
        // connections and immediately closes them, and never serves again -- so a controlled
        // restart of the resource is the only way a new config actually takes effect. See
        // ADR-019; the atomic temp-then-move write exists partly to keep that watcher quiet.
        var restart = await RestartDevProxyAsync(context, ct);
        if (!IsCurrent(context))
        {
            AddEvidence(
                Provenance(context),
                EvidenceKind.Fault,
                $"{action} discarded — the topology changed while the Dev Proxy was restarting",
                RestartCommandText,
                KnownResources.DevProxy,
                null,
                TimeSpanSince(startedAt),
                $"Captured for {context.Profile} generation {context.RunGeneration}; "
                + $"now {State.Profile} generation {State.RunGeneration}.",
                false);
            return CommandResult.Rejected(
                "The topology changed while the Dev Proxy was restarting; no level was applied to it.");
        }

        if (!restart.Succeeded)
        {
            // The config is on disk but no proxy has loaded it. Reporting it as applied would
            // be exactly the "a written config is not a live fault" lie, one step later.
            Update(current => current with
            {
                StagedFaults = retainStagedOnFailure ? current.StagedFaults : current.Applied,
                FaultDetail = restart.Message,
            });
            AddEvidence(
                Provenance(context),
                EvidenceKind.Fault,
                $"{action} written but not in force — the Dev Proxy did not restart",
                RestartCommandText,
                KnownResources.DevProxy,
                null,
                TimeSpanSince(startedAt),
                $"{result.Path} holds {applied}, but no proxy has loaded it. {restart.Message}",
                false);
            return CommandResult.Rejected(
                $"The config was written but the Dev Proxy did not come back, so no level is in force. {restart.Message}");
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
            $"{applied}{Environment.NewLine}{restart.Message}",
            true);
        return CommandResult.Ok(applied.IsAllZero
            ? "Every fault knob is at zero."
            : $"Applied {applied}. Waiting for traffic to carry it.");
    }

    private const string RestartCommandText = "aspire resource devproxy restart";

    /// <summary>
    /// Restarts the <c>devproxy</c> resource so a freshly written session config is actually
    /// loaded, through the same allow-listed Aspire command surface and the same wait-for-
    /// confirmation loop every other resource command uses.
    /// <para>
    /// This exists because Dev Proxy 3.2.0 cannot reload its own configuration: its watcher
    /// logs a restart and then leaves a proxy that accepts connections and immediately closes
    /// them. A new process with the byte-identical config works, so restarting the resource is
    /// the workaround (ADR-019). Deliberately does <b>not</b> go through
    /// <see cref="ExecuteResourceCommandAsync"/>, which would take the single-action-in-flight
    /// lock the fault controls are exempt from.
    /// </para>
    /// </summary>
    private async Task<CommandResult> RestartDevProxyAsync(OperationContext context, CancellationToken ct)
    {
        var state = State;
        if (context.Profile == TopologyProfile.None
            || !state.FaultsArmed
            || state.Ownership == TopologyOwnership.Attached)
        {
            // Nothing to restart. Apply and panic-off already refuse in these states; this is
            // the belt to that brace, and it must never silently claim a restart happened.
            return CommandResult.Rejected("There is no armed Dev Proxy in this topology to restart.");
        }

        MarkResourceTransition(KnownResources.DevProxy, ResourceCommand.Restart);
        var dispatch = await _aspire.ExecuteResourceCommandAsync(
            context.Profile,
            KnownResources.DevProxy,
            ResourceCommand.Restart,
            ct);
        if (dispatch.Status != ResourceDispatchStatus.Dispatched)
        {
            return CommandResult.Rejected($"{RestartCommandText} was {dispatch.Status}: {dispatch.Detail}");
        }

        var wait = await WaitForResourceAsync(context.Profile, KnownResources.DevProxy, ResourceCommand.Restart, ct);
        if (wait.Snapshot is not null && IsCurrent(context))
        {
            Update(current => current with { Topology = wait.Snapshot });
        }

        return wait.Confirmed
            ? CommandResult.Ok("Dev Proxy restarted and confirmed by Aspire; it loaded the new config on start.")
            : CommandResult.Rejected($"Aspire did not confirm the Dev Proxy restart: {wait.Detail}");
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

        // The sidecar is this console's child and dies with it -- an orphan daprd would keep a
        // consumer group alive against a broker nobody is watching.
        await StopOutcomeFeedAsync(ct);
        _outcomeFeed.EventReceived -= OnOutcomeEventReceived;
        _outcomeFeed.StatusChanged -= OnFeedStatusChanged;

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
            TrackSubmittedPayment(context, submission, safeResult);

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
                safeResult.Outcome is PaymentOutcome.Pending or PaymentOutcome.Completed or PaymentOutcome.Failed,
                transactionId: safeResult.TransactionId);
            return safeResult;
        }
        finally
        {
            EndMutation();
        }
    }

    // --- Outcome feedback loop ------------------------------------------------------------
    //
    // The console listens to the outcome CoreBank already broadcasts. Everything below obeys
    // three rules without exception: silence is never an outcome, the console never synthesises
    // an event it did not receive, and a contradiction between HTTP and the broadcast is shown
    // rather than resolved.

    /// <summary>
    /// Events that matched nothing when they arrived, kept briefly so a payment whose outcome
    /// was broadcast <i>before</i> its own HTTP response returned still resolves. This is
    /// correlation within one session, not replay: nothing from before the subscription started
    /// is ever in here.
    /// <para>
    /// Keyed by transaction and holding a <i>list</i>, because a settlement is three events and
    /// on the instant rail all three can beat the 200. Buffering only the terminal one left the
    /// row permanently reading "1 of 2 legs observed".
    /// </para>
    /// </summary>
    private readonly Dictionary<string, (long Arrival, List<OutcomeEvent> Events)> _unmatchedTerminalEvents =
        new(StringComparer.Ordinal);
    internal const int MaximumUnmatchedTerminalEvents = 200;
    private long _unmatchedArrivalSequence;

    /// <summary>
    /// Transactions this console submitted that no longer have a row or a live burst slot — a
    /// previous burst's ids, and rows evicted by <see cref="OperatorConsoleOptions.MaximumTrackedPayments"/>.
    /// They count for nothing, but they stop a late outcome for the console's <i>own</i> payment
    /// being announced as "not submitted from this console", which is simply false.
    /// </summary>
    private readonly BoundedTransactionIdSet _retiredTransactions = new(2000);

    /// <summary>
    /// How often, and how many times in a row, a dropped subscription is re-established.
    /// Bounded on purpose: re-establishing respawns the sidecar, and a console that retried
    /// forever would bury the evidence feed under its own failures. The budget is restored by a
    /// reconnect that works, so the cap is on consecutive failures rather than on how many
    /// outages one session may survive.
    /// </summary>
    private static readonly TimeSpan FeedReconnectInterval = TimeSpan.FromSeconds(15);
    private const int MaximumFeedReconnectAttempts = 3;

    private DateTimeOffset? _lastFeedReconnectAttempt;
    private int _feedReconnectAttempts;
    private bool _feedReconnectExhaustedReported;

    /// <summary>
    /// The in-flight reconnect, if any. <see cref="RefreshAsync"/> starts it but never awaits
    /// it: re-establishing waits on a sidecar readiness probe, and blocking the poll on that
    /// would freeze topology status and the UI behind it.
    /// </summary>
    internal Task FeedReconnectInFlight { get; private set; } = Task.CompletedTask;

    private async Task StartOutcomeFeedAsync(
        TopologyProfile profile,
        CancellationToken ct,
        bool resetCorrelation = true)
    {
        // Captured before the call: the adapter publishes its status synchronously from inside
        // StartAsync, and the handler needs a context to check staleness against.
        _feedContext = CaptureContext(State);
        if (resetCorrelation)
        {
            // A new topology generation correlates nothing from the previous one.
            lock (_unmatchedTerminalEvents)
            {
                _unmatchedTerminalEvents.Clear();
            }

            _burstTransactions.Clear();
            _retiredTransactions.Clear();
            _lastFeedReconnectAttempt = null;
            _feedReconnectAttempts = 0;
            _feedReconnectExhaustedReported = false;
        }

        try
        {
            await _outcomeFeed.StartAsync(profile, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A feed that cannot start never blocks a payment: the rows simply say so and name
            // the outcome query as the remedy.
            OnFeedStatusChanged(new OutcomeFeedStatus(OutcomeFeedState.Unavailable, Detail: ex.Message));
        }
    }

    /// <summary>
    /// Re-establishes a subscription that dropped while a topology is still running. Only from
    /// <see cref="OutcomeFeedState.Lost"/> — a feed that never came up is not retried, because
    /// its cause (a missing binary, an occupied port) does not fix itself and the retry would
    /// only spam the evidence feed. Rows already marked <c>Outcome unknown</c> stay that way:
    /// a resumed subscription is not retroactive evidence.
    /// </summary>
    private async Task TryReestablishOutcomeFeedAsync(OperationContext context, CancellationToken ct)
    {
        var state = State;
        if (state.Feed.State != OutcomeFeedState.Lost
            || state.Profile == TopologyProfile.None
            || !IsCurrent(context))
        {
            return;
        }

        if (_feedReconnectAttempts >= MaximumFeedReconnectAttempts)
        {
            // Giving up quietly would leave the console looking like it was still trying.
            if (!_feedReconnectExhaustedReported)
            {
                _feedReconnectExhaustedReported = true;
                AddEvidence(
                    FeedProvenance(context),
                    EvidenceKind.OutcomeEvent,
                    OutcomeFeedNarrative.ReconnectExhausted(_feedReconnectAttempts),
                    "SUBSCRIBE",
                    OutcomeEventTypes.Topic,
                    null,
                    TimeSpan.Zero,
                    state.Feed.Detail,
                    false);
            }

            return;
        }

        var now = _time.GetUtcNow();
        if (_lastFeedReconnectAttempt is { } last && now - last < FeedReconnectInterval)
        {
            return;
        }

        _lastFeedReconnectAttempt = now;
        _feedReconnectAttempts++;
        await StartOutcomeFeedAsync(state.Profile, ct, resetCorrelation: false);
    }

    private async Task StopOutcomeFeedAsync(CancellationToken ct)
    {
        _feedContext = null;
        lock (_unmatchedTerminalEvents)
        {
            _unmatchedTerminalEvents.Clear();
        }

        _burstTransactions.Clear();
        _retiredTransactions.Clear();
        try
        {
            await _outcomeFeed.StopAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddEvidence(
                EvidenceKind.OutcomeEvent,
                "The console's own Dapr sidecar did not stop cleanly",
                "STOP",
                OutcomeEventTypes.Topic,
                null,
                TimeSpan.Zero,
                ex.Message,
                false);
        }
    }

    /// <summary>
    /// The single place the feed's own state reaches the console. A drop is structural, not
    /// decorative: every unresolved row <b>changes state</b>, so the words "Awaiting settlement"
    /// leave the screen at the instant they stop being true.
    /// </summary>
    private void OnFeedStatusChanged(OutcomeFeedStatus status)
    {
        var context = _feedContext;
        if (context is null || !IsCurrent(context))
        {
            return;
        }

        if (status.State == OutcomeFeedState.Listening)
        {
            // A reconnect that worked restores the full budget: the cap is on consecutive
            // failures, not on how many outages one session may survive.
            _feedReconnectAttempts = 0;
            _lastFeedReconnectAttempt = null;
            _feedReconnectExhaustedReported = false;
        }

        var withdrawn = 0;
        Update(state =>
        {
            if (status.State != OutcomeFeedState.Lost)
            {
                return state with { Feed = status };
            }

            // A8: only the rows this drop actually withdrew. Counting every historical unknown
            // row would attribute a previous outage's casualties to this one.
            withdrawn = state.TrackedPayments.Count(payment => payment.IsOutstanding);
            var stoppedAt = OutcomeFeedNarrative.Clock(status.LostAt ?? _time.GetUtcNow());
            var payments = state.TrackedPayments
                .Select(payment => payment.IsOutstanding
                    ? payment with
                    {
                        State = PaymentTrackingState.OutcomeUnknown,
                        Note = $"the console stopped listening at {stoppedAt}",
                    }
                    : payment)
                .ToList();

            // A7: the burst's proven leg is a claim about the same feed. Leaving "awaiting N"
            // on screen with nobody listening is the very reading this feature removes from the
            // payment rows, so the burst withdraws it in the same breath.
            var burst = state.Burst.Outstanding > 0
                ? state.Burst with { Unknown = state.Burst.Unknown + state.Burst.Outstanding }
                : state.Burst;
            return state with { Feed = status, TrackedPayments = payments, Burst = burst };
        });

        var (summary, succeeded) = status.State switch
        {
            OutcomeFeedState.Listening when status.GapStart is not null || status.GapEnd is not null =>
                (OutcomeFeedNarrative.ListeningAgain(status.GapStart, status.GapEnd), true),
            OutcomeFeedState.Listening =>
                (OutcomeFeedNarrative.ListeningSince(status.ListeningSince), true),
            OutcomeFeedState.Lost =>
                (OutcomeFeedNarrative.FeedLost(status.LostAt, withdrawn), false),
            OutcomeFeedState.Unavailable =>
                (OutcomeFeedNarrative.Unavailable(status.Detail), false),
            _ => (string.Empty, true),
        };
        if (summary.Length == 0)
        {
            return;
        }

        AddEvidence(
            FeedProvenance(context),
            EvidenceKind.OutcomeEvent,
            summary,
            "SUBSCRIBE",
            OutcomeEventTypes.Topic,
            null,
            TimeSpan.Zero,
            status.Detail,
            succeeded);
    }

    /// <summary>
    /// Provenance for a record the feed produced. The profile and generation come from the
    /// subscription's context, but the <b>fault levels are read now</b>: a level armed after
    /// the subscription started is still in force when the event lands, and stamping the
    /// levels frozen at subscribe time would file every such record as fault-free.
    /// </summary>
    private EvidenceProvenance FeedProvenance(OperationContext context) =>
        new(context.Profile, context.RunGeneration, FaultsInForce(State));

    /// <summary>
    /// One arriving event. Resolves its row <b>in place</b> — same list index, no re-sort, no
    /// scroll, no focus change — and appends to Evidence whether or not it matched anything.
    /// </summary>
    private void OnOutcomeEventReceived(OutcomeEvent outcomeEvent)
    {
        var context = _feedContext;
        if (context is null || !IsCurrent(context))
        {
            // The event belongs to a topology this console has since stopped or switched away
            // from. Stamping it onto the current one would be a claim about the wrong system.
            return;
        }

        var observedAt = _time.GetUtcNow();
        var attribution = EventAttribution.Unattributed;
        Update(state =>
        {
            var index = IndexOfTrackedPayment(state, outcomeEvent.TransactionId);
            if (index < 0)
            {
                return state;
            }

            attribution = EventAttribution.Tracked;
            var payments = state.TrackedPayments.ToList();
            payments[index] = ResolveTrackedPayment(payments[index], outcomeEvent, observedAt);
            return state with { TrackedPayments = payments };
        });

        if (attribution == EventAttribution.Unattributed
            && _burstTransactions.TryGetValue(outcomeEvent.TransactionId, out var burstTransaction))
        {
            attribution = EventAttribution.Tracked;
            CountForBurst(outcomeEvent, burstTransaction, context);
        }

        if (attribution == EventAttribution.Unattributed
            && _retiredTransactions.Contains(outcomeEvent.TransactionId))
        {
            // A3/A4: this console did submit it -- in a previous burst, or in a row since
            // evicted. It counts for nothing, but calling it a stranger's transaction is false.
            attribution = EventAttribution.Retired;
        }

        if (attribution == EventAttribution.Unattributed)
        {
            // A5: legs are buffered too. On the instant rail all three events can beat the 200,
            // and dropping the two legs left the row reading "1 of 2 legs observed" for ever.
            RememberUnmatchedEvent(outcomeEvent);
        }

        AddEvidence(
            FeedProvenance(context),
            EvidenceKind.OutcomeEvent,
            EventSummary(outcomeEvent, attribution),
            // The meta line always prints the CloudEvent type verbatim and the transaction id.
            outcomeEvent.EventType,
            outcomeEvent.TransactionId,
            null,
            // The delivery delta, which is the transport's and never the bank's processing
            // time. Deliberately not admitted as proof that injected faults reached traffic:
            // Dev Proxy watches CoreBankAPI's HTTP surface, and the broadcast never crosses it
            // (see CarriesAppliedFaults, which excludes this kind).
            DeliveryDelta(outcomeEvent, observedAt),
            EventDetail(outcomeEvent, observedAt),
            outcomeEvent.Failed is null,
            outcomeEvent.TransactionId,
            // Never steals the Details pane from a record the operator is reading.
            select: false);
    }

    private static TimeSpan DeliveryDelta(OutcomeEvent outcomeEvent, DateTimeOffset observedAt) =>
        outcomeEvent.ProcessedAt is { } processedAt && observedAt > processedAt
            ? observedAt - processedAt
            : TimeSpan.Zero;

    /// <summary>How much of a claim the console may make about one arriving event.</summary>
    private enum EventAttribution
    {
        /// <summary>Matches no payment this console submitted.</summary>
        Unattributed,

        /// <summary>Matches a live row or the burst currently on screen.</summary>
        Tracked,

        /// <summary>This console's own payment, but no longer on screen or counted.</summary>
        Retired,
    }

    /// <summary>
    /// Applies one event to one row. Idempotent for a redelivered terminal event and for a
    /// redelivered leg, because Dapr delivery is at-least-once and a second copy of a leg is
    /// not a second leg.
    /// </summary>
    private static TrackedPayment ResolveTrackedPayment(
        TrackedPayment payment,
        OutcomeEvent outcomeEvent,
        DateTimeOffset observedAt)
    {
        if (outcomeEvent.BalanceUpdated is { } leg)
        {
            if (payment.State == PaymentTrackingState.Rejected)
            {
                // A rejection emits no balance legs, and the row says so. Attaching one anyway
                // would put a leg column beside the words "a rejection emits none". The event
                // is still in the Evidence feed, where the disagreement is visible.
                return payment;
            }

            var legs = payment.ObservedLegs;
            if (legs.Any(existing =>
                    string.Equals(existing.AccountNumber, leg.AccountNumber, StringComparison.Ordinal)
                    && existing.Delta == leg.Delta
                    && existing.NewBalance == leg.NewBalance))
            {
                return payment;
            }

            return payment with
            {
                Legs = [.. legs, new SettlementLeg(leg.AccountNumber, leg.Delta, leg.NewBalance, leg.Currency, observedAt)],
            };
        }

        if (payment.BroadcastOutcome is not null)
        {
            return payment;
        }

        if (outcomeEvent.Completed is { } completed)
        {
            // HTTP already proved a rejection and the broadcast says otherwise. Both records
            // stay, both stay labelled, and the console picks no winner.
            var contradicts = payment.HttpOutcome == PaymentOutcome.Failed;
            return payment with
            {
                BroadcastOutcome = PaymentOutcome.Completed,
                State = contradicts ? PaymentTrackingState.Contradiction : PaymentTrackingState.Settled,
                ProcessedAt = completed.ProcessedAt,
                ObservedAt = observedAt,
                Note = contradicts ? "HTTP proved Failed, broadcast says Completed" : null,
            };
        }

        var failed = outcomeEvent.Failed!;
        var failureContradicts = payment.HttpOutcome == PaymentOutcome.Completed;
        return payment with
        {
            BroadcastOutcome = PaymentOutcome.Failed,
            State = failureContradicts ? PaymentTrackingState.Contradiction : PaymentTrackingState.Rejected,
            ProcessedAt = failed.ProcessedAt,
            ObservedAt = observedAt,
            ErrorReason = failed.ErrorReason,
            Note = failureContradicts ? "HTTP proved Completed, broadcast says Failed" : null,
        };
    }

    /// <summary>
    /// Moves the burst's proven leg. Only a terminal event moves it, only once per transaction,
    /// and only for the burst currently on screen.
    /// </summary>
    private void CountForBurst(OutcomeEvent outcomeEvent, BurstTransaction transaction, OperationContext context)
    {
        if (!outcomeEvent.IsTerminal
            || transaction.BurstNumber != Volatile.Read(ref _activeBurstNumber)
            || !transaction.TryMarkResolved())
        {
            return;
        }

        var settled = outcomeEvent.Completed is not null;
        Update(state => IsCurrent(context)
            ? state with
            {
                Burst = settled
                    ? state.Burst with { Settled = state.Burst.Settled + 1 }
                    : state.Burst with { Rejected = state.Burst.Rejected + 1 },
            }
            : state);
    }

    private void TrackSubmittedPayment(
        OperationContext context,
        PaymentSubmission submission,
        PaymentResult result)
    {
        if (result.TransactionId is not { Length: > 0 } transactionId)
        {
            // No id means nothing to correlate by, and this console introduces no second
            // correlation identifier to work around that.
            return;
        }

        var submittedAt = _time.GetUtcNow();
        var buffered = TakeBufferedEvents(transactionId);
        var applied = false;
        var evicted = new List<string>();

        Update(state =>
        {
            if (!IsCurrent(context))
            {
                return state;
            }

            var payments = state.TrackedPayments.ToList();
            var index = payments.FindIndex(payment =>
                string.Equals(payment.TransactionId, transactionId, StringComparison.Ordinal));
            if (index >= 0)
            {
                // A resend of the same key returns the same transaction; it updates the row it
                // already has rather than adding a second one for the same payment -- a
                // duplicate row could never resolve and would read "Awaiting settlement" for ever.
                payments[index] = ApplyBuffered(
                    payments[index] with
                    {
                        HttpOutcome = result.Outcome,
                        HttpStatusCode = result.StatusCode,
                    },
                    buffered,
                    submittedAt);
                applied = buffered.Count > 0;
                return state with { TrackedPayments = payments };
            }

            // A11: an Ambiguous or transport-failed submission that still returned a
            // TransactionId is the case that needs the feed most. It gets a row, but never the
            // words "Awaiting settlement": its own HTTP leg never proved it was accepted.
            var proven = result.Outcome is PaymentOutcome.Pending
                or PaymentOutcome.Completed
                or PaymentOutcome.Failed;
            var listening = state.Feed.IsListening && proven;
            var row = new TrackedPayment(
                Interlocked.Increment(ref _paymentSequence),
                transactionId,
                submission.Request.Rail,
                submission.Request.Amount,
                submission.Request.Currency,
                submission.Request.FromAccount,
                submission.Request.ToAccount,
                submittedAt,
                result.Outcome,
                result.StatusCode,
                // Never "Awaiting settlement" without a feed: nothing is awaiting anything.
                listening ? PaymentTrackingState.Awaiting : PaymentTrackingState.NotObserved,
                Note: listening
                    ? null
                    : proven
                        ? NoFeedNote(state.Feed)
                        : $"the submission's own outcome was {result.Outcome} — only an outcome query can move it forward");
            row = ApplyBuffered(row, buffered, submittedAt);
            applied = buffered.Count > 0;

            payments.Add(row);
            if (payments.Count > _options.MaximumTrackedPayments)
            {
                var overflow = payments.Count - _options.MaximumTrackedPayments;
                evicted.AddRange(payments.Take(overflow).Select(payment => payment.TransactionId));
                payments.RemoveRange(0, overflow);
            }

            return state with { TrackedPayments = payments };
        });

        // A4: an evicted row's later broadcast is still this console's own payment.
        foreach (var id in evicted)
        {
            _retiredTransactions.Add(id);
        }

        if (!applied)
        {
            // A6: the buffer was read before the update, and the update may have declined to
            // use it (a stale context). Putting it back keeps a real outcome available to the
            // submission that eventually claims it, instead of silently discarding it.
            RestoreBufferedEvents(transactionId, buffered);
            return;
        }

        AddEvidence(
            FeedProvenance(context),
            EvidenceKind.OutcomeEvent,
            $"{transactionId} attributed — its broadcast outcome arrived before this console's own submission response",
            buffered[0].EventType,
            transactionId,
            null,
            TimeSpan.Zero,
            "Correlated within this session; no event from before the subscription started was used.",
            true,
            transactionId,
            select: false);
    }

    private static TrackedPayment ApplyBuffered(
        TrackedPayment row,
        IReadOnlyList<OutcomeEvent> buffered,
        DateTimeOffset observedAt)
    {
        foreach (var outcomeEvent in buffered)
        {
            row = ResolveTrackedPayment(row, outcomeEvent, observedAt);
        }

        return row;
    }

    /// <summary>
    /// Claims buffered events for a burst id registered after they arrived. Same within-session
    /// correlation as <see cref="TrackSubmittedPayment"/>, never replay.
    /// </summary>
    private void ResolveBufferedBurstEvents(string transactionId, int burstNumber)
    {
        // A6: every reason to decline is checked *before* the buffer is read, so a mismatch
        // can never consume and discard a real outcome.
        if (_feedContext is not { } context
            || !_burstTransactions.TryGetValue(transactionId, out var transaction)
            || transaction.BurstNumber != burstNumber)
        {
            return;
        }

        var buffered = TakeBufferedEvents(transactionId);
        foreach (var outcomeEvent in buffered)
        {
            CountForBurst(outcomeEvent, transaction, context);
        }
    }

    /// <summary>
    /// Claims every buffered event for <paramref name="transactionId"/>. Removing them as they
    /// are read keeps the buffer to events still waiting for a submission.
    /// </summary>
    private IReadOnlyList<OutcomeEvent> TakeBufferedEvents(string transactionId)
    {
        lock (_unmatchedTerminalEvents)
        {
            return _unmatchedTerminalEvents.Remove(transactionId, out var entry) ? entry.Events : [];
        }
    }

    /// <summary>
    /// Puts back events that were read but not applied, keeping their original arrival order so
    /// eviction pressure still falls on the oldest.
    /// </summary>
    private void RestoreBufferedEvents(string transactionId, IReadOnlyList<OutcomeEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        foreach (var outcomeEvent in events)
        {
            RememberUnmatchedEvent(outcomeEvent);
        }
    }

    private void RememberUnmatchedEvent(OutcomeEvent outcomeEvent)
    {
        lock (_unmatchedTerminalEvents)
        {
            if (_unmatchedTerminalEvents.TryGetValue(outcomeEvent.TransactionId, out var existing))
            {
                existing.Events.Add(outcomeEvent);
                return;
            }

            if (_unmatchedTerminalEvents.Count >= MaximumUnmatchedTerminalEvents)
            {
                // Evict by recorded arrival, not by enumeration order: a Dictionary that has
                // had entries removed no longer enumerates in insertion order, so `Keys.First()`
                // would drop an arbitrary event — quite possibly the newest, the one most likely
                // still to be waiting for its own HTTP response.
                var oldest = _unmatchedTerminalEvents
                    .OrderBy(entry => entry.Value.Arrival)
                    .First().Key;
                _unmatchedTerminalEvents.Remove(oldest);
            }

            _unmatchedTerminalEvents[outcomeEvent.TransactionId] =
                (++_unmatchedArrivalSequence, [outcomeEvent]);
        }
    }

    private static int IndexOfTrackedPayment(OperatorConsoleState state, string transactionId)
    {
        for (var index = 0; index < state.TrackedPayments.Count; index++)
        {
            if (string.Equals(state.TrackedPayments[index].TransactionId, transactionId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    internal static string NoFeedNote(OutcomeFeedStatus feed) => feed.State switch
    {
        OutcomeFeedState.Lost =>
            $"the console stopped listening at {OutcomeFeedNarrative.Clock(feed.LostAt)}",
        OutcomeFeedState.Unavailable => feed.Detail.Length > 0
            ? feed.Detail
            : "no subscription to transaction-events could be established",
        _ => "the console is not subscribed to transaction-events",
    };

    /// <summary>
    /// Past-tense grammar for something the system said, and the full "not submitted from this
    /// console" label when it matched nothing. Dropping an unattributed event would make the
    /// feed a lie by omission; attributing it would make it a lie outright.
    /// </summary>
    private static string EventSummary(OutcomeEvent outcomeEvent, EventAttribution attribution)
    {
        var subject = outcomeEvent.EventType switch
        {
            OutcomeEventTypes.TransactionCompleted => $"Settled — {outcomeEvent.TransactionId}",
            OutcomeEventTypes.TransactionFailed =>
                $"Rejected — {outcomeEvent.TransactionId} · ErrorReason: {outcomeEvent.Failed?.ErrorReason ?? "(none supplied)"}",
            OutcomeEventTypes.BalanceUpdated =>
                $"Balance updated — {outcomeEvent.TransactionId} · {LegText(outcomeEvent)}",
            // A type this console does not model is still printed verbatim rather than
            // flattened into one it happens to know.
            _ => $"{outcomeEvent.EventType} — {outcomeEvent.TransactionId}",
        };
        return attribution switch
        {
            EventAttribution.Tracked => subject,
            EventAttribution.Retired =>
                $"{subject} · from a payment this console submitted earlier this session",
            _ => $"{subject} · Unattributed — {outcomeEvent.TransactionId} was not submitted from this console",
        };
    }

    private static string LegText(OutcomeEvent outcomeEvent) =>
        outcomeEvent.BalanceUpdated is { } leg
            ? new SettlementLeg(leg.AccountNumber, leg.Delta, leg.NewBalance, leg.Currency, default).ToString()
            : string.Empty;

    /// <summary>
    /// Two clocks, never one: the event's own <c>ProcessedAt</c> and the console's observed-at
    /// time, with the delta explicit. Delivery latency belongs to the transport, and presenting
    /// it as the bank's processing time would be a lie of the same class as a written fault
    /// config reported as a live fault.
    /// </summary>
    private static string EventDetail(OutcomeEvent outcomeEvent, DateTimeOffset observedAt)
    {
        var lines = new List<string> { outcomeEvent.EventType, $"TransactionId: {outcomeEvent.TransactionId}" };
        if (outcomeEvent.ProcessedAt is { } processedAt)
        {
            lines.Add($"ProcessedAt {OutcomeFeedNarrative.PreciseClock(processedAt)}, observed here "
                + $"{OutcomeFeedNarrative.PreciseClock(observedAt)} "
                + $"(+{(observedAt - processedAt).TotalMilliseconds:F0} ms)");
        }
        else
        {
            lines.Add($"Observed here {OutcomeFeedNarrative.PreciseClock(observedAt)}; "
                + "this event type carries no ProcessedAt.");
        }

        if (outcomeEvent.Completed is { } completed)
        {
            lines.Add($"Status: {completed.Status ?? "(none supplied)"}");
        }

        if (outcomeEvent.Failed is { } failed)
        {
            lines.Add($"Status: {failed.Status ?? "(none supplied)"}");
            lines.Add($"ErrorReason: {failed.ErrorReason ?? "(none supplied)"}");
        }

        if (outcomeEvent.BalanceUpdated is not null)
        {
            lines.Add(LegText(outcomeEvent));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// A bounded, insertion-ordered set of transaction ids. Bounded because a long session with
    /// repeated bursts would otherwise grow it without limit; insertion-ordered so the ids
    /// dropped under pressure are the oldest, which are the least likely to still produce an
    /// outcome.
    /// </summary>
    private sealed class BoundedTransactionIdSet(int capacity)
    {
        private readonly Queue<string> _order = new();
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly object _sync = new();

        public void Add(string transactionId)
        {
            lock (_sync)
            {
                if (!_ids.Add(transactionId))
                {
                    return;
                }

                _order.Enqueue(transactionId);
                while (_order.Count > capacity)
                {
                    _ids.Remove(_order.Dequeue());
                }
            }
        }

        public bool Contains(string transactionId)
        {
            lock (_sync)
            {
                return _ids.Contains(transactionId);
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _order.Clear();
                _ids.Clear();
            }
        }
    }

    /// <summary>
    /// A burst transaction and whether its proven-leg counter has already moved. Dapr delivery
    /// is at-least-once, so a redelivered terminal event must not be counted twice.
    /// </summary>
    private sealed class BurstTransaction(int burstNumber)
    {
        private int _resolved;

        public int BurstNumber { get; } = burstNumber;

        public bool TryMarkResolved() => Interlocked.CompareExchange(ref _resolved, 1, 0) == 0;
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
            // A new generation starts with no payment history, exactly as a relaunch does:
            // nothing from the previous topology may be read as current.
            TrackedPayments = [],
            Feed = OutcomeFeedStatus.NotStarted,
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
        bool succeeded,
        string? transactionId = null)
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
            succeeded,
            transactionId);
    }

    /// <param name="select">
    /// False for records the operator did not ask for. An arriving broadcast outcome claims the
    /// evidence strip, but it must never yank the Details pane away from a record the operator
    /// is reading -- a pushed outcome never steals attention.
    /// </param>
    private void AddEvidence(
        EvidenceProvenance provenance,
        EvidenceKind kind,
        string summary,
        string method,
        string target,
        int? statusCode,
        TimeSpan duration,
        string detail,
        bool succeeded,
        string? transactionId = null,
        bool select = true)
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
                provenance.Faults,
                transactionId);
            records.Add(record);
            if (records.Count > _options.MaximumEvidenceRecords)
            {
                records.RemoveRange(0, records.Count - _options.MaximumEvidenceRecords);
            }

            // C7: the operator's selection can be trimmed away by a flood of inbound events.
            // Showing a details pane for a record no longer in the list is worse than falling
            // back to the newest one that is.
            var selected = select || (state.SelectedEvidence is { } current && records.Contains(current))
                ? select ? record : state.SelectedEvidence
                : record;
            return state with
            {
                Evidence = records,
                SelectedEvidence = selected,
                FaultsObserved = state.FaultsObserved || CarriesAppliedFaults(state, record),
                // A1: an arriving outcome claims the evidence strip (which reads the newest
                // record) but never rewrites the mutation status line under the operator --
                // that line belongs to what the operator is doing.
                StatusLine = select
                    ? $"{KnownTopologyProfiles.DisplayName(record.Profile)} · generation {record.RunGeneration} · {record.Summary}"
                    : state.StatusLine,
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
