using System.Collections.Concurrent;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Application;

public sealed record CommandResult(bool Succeeded, string Message)
{
    public static CommandResult Ok(string message) => new(true, message);
    public static CommandResult Rejected(string message) => new(false, message);
}

public sealed class OperatorConsoleController
{
    private readonly IAspireAdapter _aspire;
    private readonly IProcessAdapter _processes;
    private readonly IPaymentGateway _payments;
    private readonly ILoadWorkflowRunner _loadWorkflow;
    private readonly IEvidenceExporter _exporter;
    private readonly IBrowserLauncher _browser;
    private readonly TimeProvider _time;
    private readonly OperatorConsoleOptions _options;
    private readonly Func<string> _keyFactory;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly object _sync = new();
    private readonly TopologyObservationDebouncer _debouncer = new();

    private OperatorConsoleState _state = OperatorConsoleState.Empty;
    private TopologyHandle? _ownedHandle;
    private CancellationTokenSource? _burstCancellation;
    private long _evidenceSequence;
    private int _burstSequence;

    public OperatorConsoleController(
        IAspireAdapter aspire,
        IProcessAdapter processes,
        IPaymentGateway payments,
        ILoadWorkflowRunner loadWorkflow,
        IEvidenceExporter exporter,
        IBrowserLauncher browser,
        TimeProvider time,
        OperatorConsoleOptions? options = null,
        Func<string>? keyFactory = null)
    {
        _aspire = aspire;
        _processes = processes;
        _payments = payments;
        _loadWorkflow = loadWorkflow;
        _exporter = exporter;
        _browser = browser;
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

    public async Task InitializeAsync(CancellationToken ct)
    {
        var discovered = await _aspire.DiscoverAsync(ct);
        var matches = discovered.Where(snapshot => snapshot.IsReady).ToList();

        if (discovered.Count > 1)
        {
            Update(state => state with
            {
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
                StatusLine = "No topology active. Preflight is ready; Start or Attach is explicit.",
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

        var observed = await _aspire.GetSnapshotAsync(state.Profile, ct);
        if (state.Ownership == TopologyOwnership.Attached && !observed.IsReachable)
        {
            var discovered = await _aspire.DiscoverAsync(ct);
            if (discovered.All(snapshot => snapshot.Profile != state.Profile))
            {
                _debouncer.Reset();
                Update(current => current with
                {
                    Profile = TopologyProfile.None,
                    Ownership = TopologyOwnership.None,
                    Topology = null,
                    ResourceAuthorityAvailable = false,
                    LastPayment = null,
                    CanResendLastPayment = false,
                    LastLoadResult = null,
                    LoadProgress = new LoadWorkflowProgress(LoadWorkflowPhase.NotStarted, TimeSpan.Zero, "Not run this session."),
                    StatusLine = "The attached AppHost is no longer running. Start or attach a known topology.",
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

        if (!TryBeginMutation(MutationKind.StartTopology, KnownTopologyProfiles.DisplayName(profile), out var mutation))
        {
            return BusyResult();
        }

        try
        {
            var handle = await _processes.StartOwnedAsync(profile, ct);
            _ownedHandle = handle;
            SetTopologyTransition(profile, TopologyOwnership.Owned, "Starting AppHost");
            var snapshot = await WaitForTopologyAsync(profile, expectPresent: true, ct);
            if (snapshot is null)
            {
                var detail = JournalRedaction.Apply(_processes.GetRecentOutput(handle));
                AddEvidence(EvidenceKind.Topology, $"Start {profile} timed out", "aspire run", profile.ToString(), null, TimeSpanSince(mutation.StartedAt), detail, false);
                return CommandResult.Rejected($"Timed out waiting for {profile}. {detail}");
            }

            ActivateTopology(snapshot, TopologyOwnership.Owned);
            AddEvidence(EvidenceKind.Topology, $"Started {profile} as Owned", "aspire run", profile.ToString(), null, TimeSpanSince(mutation.StartedAt), snapshot.Fingerprint, true);
            return CommandResult.Ok($"{profile} started and verified.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AddEvidence(EvidenceKind.Topology, $"Start {profile} failed", "aspire run", profile.ToString(), null, TimeSpanSince(mutation.StartedAt), ex.Message, false);
            return CommandResult.Rejected(ex.Message);
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
        if (state.Ownership != TopologyOwnership.Owned || _ownedHandle is null)
        {
            return CommandResult.Rejected("Whole-AppHost Stop is available only for an AppHost owned by this session.");
        }

        if (!TryBeginMutation(MutationKind.StopTopology, state.Profile.ToString(), out var mutation))
        {
            return BusyResult();
        }

        try
        {
            await _processes.StopOwnedAsync(_ownedHandle, ct);
            var stoppedProfile = state.Profile;
            AddEvidence(EvidenceKind.Topology, $"Stopped {stoppedProfile}", "owned child stop", stoppedProfile.ToString(), null, TimeSpanSince(mutation.StartedAt), "Owned process stopped gracefully.", true);
            _ownedHandle = null;
            _debouncer.Reset();
            Update(current => current with
            {
                Profile = TopologyProfile.None,
                Ownership = TopologyOwnership.None,
                Topology = null,
                ResourceAuthorityAvailable = false,
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

        if (!TryBeginMutation(MutationKind.SwitchTopology, target.ToString(), out var mutation))
        {
            return BusyResult();
        }

        try
        {
            if (_ownedHandle is not null)
            {
                await _processes.StopOwnedAsync(_ownedHandle, ct);
                _ownedHandle = null;
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
                });
            }

            var handle = await _processes.StartOwnedAsync(target, ct);
            _ownedHandle = handle;
            SetTopologyTransition(target, TopologyOwnership.Owned, "Switching AppHost");
            var snapshot = await WaitForTopologyAsync(target, expectPresent: true, ct);
            if (snapshot is null)
            {
                AddEvidence(EvidenceKind.Topology, $"Switch to {target} timed out", "aspire run", target.ToString(), null, TimeSpanSince(mutation.StartedAt), JournalRedaction.Apply(_processes.GetRecentOutput(handle)), false);
                return CommandResult.Rejected($"Timed out switching to {target}.");
            }

            ActivateTopology(snapshot, TopologyOwnership.Owned);
            AddEvidence(EvidenceKind.Topology, $"Switched to {target}", "owned stop + aspire run", target.ToString(), null, TimeSpanSince(mutation.StartedAt), snapshot.Fingerprint, true);
            return CommandResult.Ok($"Switched to {target}.");
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
            if (!dispatch.Dispatched)
            {
                AddEvidence(EvidenceKind.Resource, $"{command} {resourceName} rejected", $"aspire resource {resourceName} {command.ToString().ToLowerInvariant()}", resourceName, null, TimeSpanSince(mutation.StartedAt), dispatch.Detail, false);
                return CommandResult.Rejected(dispatch.Detail);
            }

            var snapshot = await WaitForResourceAsync(state.Profile, resourceName, command, ct);
            if (snapshot is null)
            {
                AddEvidence(EvidenceKind.Resource, $"{command} {resourceName} timed out", $"aspire resource {resourceName} {command.ToString().ToLowerInvariant()}", resourceName, null, TimeSpanSince(mutation.StartedAt), "Aspire did not confirm the requested terminal state.", false);
                return CommandResult.Rejected("Timed out waiting for a fresh Aspire snapshot to confirm the resource transition.");
            }

            Update(current => current with { Topology = snapshot });
            AddEvidence(EvidenceKind.Resource, $"{command} {resourceName} confirmed", $"aspire resource {resourceName} {command.ToString().ToLowerInvariant()}", resourceName, null, TimeSpanSince(mutation.StartedAt), dispatch.Detail, true);
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
        if (State.Profile == TopologyProfile.None || State.Ownership == TopologyOwnership.None)
        {
            return new InspectionResult(false, 0, "outcome", null, "Start or attach a topology before querying an outcome.", TimeSpan.Zero);
        }

        if (string.IsNullOrWhiteSpace(transactionIdOrKey))
        {
            return new InspectionResult(false, 0, "outcome", null, "Enter a transaction id or idempotency key.", TimeSpan.Zero);
        }

        var startedAt = _time.GetUtcNow();
        var result = await _payments.QueryOutcomeAsync(State.Profile, transactionIdOrKey.Trim(), ct);
        AddEvidence(
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
        if (State.Profile == TopologyProfile.None || State.Ownership == TopologyOwnership.None)
        {
            return new InspectionResult(false, 0, endpointId, null, "Start or attach a topology before inspecting evidence.", TimeSpan.Zero);
        }

        var result = await _payments.InspectAsync(State.Profile, endpointId, ct);
        AddEvidence(
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
                    var key = $"demo-burst-{_sessionId}-g{State.RunGeneration:D3}-r{burstNumber:D3}-{index:D6}";
                    PaymentResult result;
                    try
                    {
                        result = EnforceRailSemantics(
                            template.Rail,
                            await _payments.SubmitAsync(
                                State.Profile,
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

                    Update(state => state with
                    {
                        Burst = new BurstProgress(count, sent, accepted, completed, failed, false),
                    });
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
            AddEvidence(EvidenceKind.Burst, summary, "POST", KnownEndpoints.PaymentsSubmit, null, TimeSpanSince(mutation.StartedAt), string.Join(Environment.NewLine, failures), !final.Cancelled && final.Failed == 0);
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

        try
        {
            var progress = new InlineProgress<LoadWorkflowProgress>(value =>
                Update(current => current with
                {
                    LoadProgress = value,
                    StatusLine = $"Load Test · {value.Phase} — {value.Detail}",
                }));

            var result = await _loadWorkflow.RunAsync(expectedUniqueCount, progress, ct);
            Update(current => current with { LastLoadResult = result });
            AddEvidence(
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
        var result = await _exporter.ExportAsync(State.Evidence, ct);
        AddEvidence(
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

    public Task<bool> OpenKnownLinkAsync(string linkId, CancellationToken ct)
    {
        if (!KnownLinks.All.Contains(linkId))
        {
            return Task.FromResult(false);
        }

        var resolvedUrl = linkId == KnownLinks.AspireDashboard
            ? State.Topology?.DashboardUrl
            : null;
        return _browser.OpenAsync(linkId, resolvedUrl, ct);
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        CancelActiveBurst();
        if (_ownedHandle is not null)
        {
            await _processes.StopOwnedAsync(_ownedHandle, ct);
            _ownedHandle = null;
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

        if (State.Profile == TopologyProfile.None || State.Ownership == TopologyOwnership.None)
        {
            return RejectedPayment("Start or attach a topology before submitting a payment.");
        }

        if (!TryBeginMutation(MutationKind.SubmitPayment, isResend ? "Resend payment" : "Submit payment", out var mutation))
        {
            return RejectedPayment(BusyResult().Message);
        }

        try
        {
            var result = await _payments.SubmitAsync(State.Profile, submission, ct);
            var safeResult = EnforceRailSemantics(submission.Request.Rail, result);
            var canResend = submission.IdempotencyMode != IdempotencyMode.Omitted;
            if (submission.IdempotencyMode == IdempotencyMode.Omitted && safeResult.IsAmbiguous)
            {
                canResend = false;
            }

            Update(state => state with
            {
                LastPayment = submission,
                CanResendLastPayment = canResend,
            });

            var summary = safeResult.Outcome switch
            {
                PaymentOutcome.Pending => $"{safeResult.StatusCode} Pending — no committed outcome yet",
                PaymentOutcome.Ambiguous => "Ambiguous — not yet reconciled; Resend is unsafe",
                PaymentOutcome.Completed => $"{safeResult.StatusCode} Completed",
                PaymentOutcome.Failed => $"{safeResult.StatusCode} Failed",
                _ => safeResult.ErrorSummary ?? safeResult.Outcome.ToString(),
            };
            AddEvidence(
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

    private async Task<TopologySnapshot?> WaitForResourceAsync(
        TopologyProfile profile,
        string resourceName,
        ResourceCommand command,
        CancellationToken ct)
    {
        var startedAt = _time.GetUtcNow();
        var deadline = _time.GetUtcNow() + _options.TransitionTimeout;
        for (var attempt = 0; attempt < 240 && _time.GetUtcNow() <= deadline; attempt++)
        {
            var snapshot = await _aspire.GetSnapshotAsync(profile, ct);
            var resource = snapshot.FindResource(resourceName);
            if (snapshot.IsReachable && snapshot.IsFingerprintMatch && ResourceReachedTarget(resource, command))
            {
                return snapshot;
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

        return null;
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
            if (_state.ActiveMutation is not null)
            {
                mutation = _state.ActiveMutation;
                return false;
            }

            mutation = new ActiveMutation(kind, target, _time.GetUtcNow());
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
        Update(state => state with
        {
            ActiveMutation = null,
            StatusLine = state.Evidence.LastOrDefault()?.Summary ?? state.StatusLine,
        });
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
        Update(state =>
        {
            var records = state.Evidence.ToList();
            var record = new EvidenceRecord(
                Interlocked.Increment(ref _evidenceSequence),
                _time.GetUtcNow(),
                state.Profile,
                state.RunGeneration,
                kind,
                JournalRedaction.Apply(summary),
                method,
                target,
                statusCode,
                duration,
                JournalRedaction.Apply(detail ?? string.Empty),
                succeeded);
            records.Add(record);
            if (records.Count > _options.MaximumEvidenceRecords)
            {
                records.RemoveRange(0, records.Count - _options.MaximumEvidenceRecords);
            }

            return state with
            {
                Evidence = records,
                SelectedEvidence = record,
                StatusLine = $"{KnownTopologyProfiles.DisplayName(record.Profile)} · generation {record.RunGeneration} · {record.Summary}",
            };
        });
    }

    private TimeSpan TimeSpanSince(DateTimeOffset startedAt) => _time.GetUtcNow() - startedAt;

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
}
