using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Application.Scenarios;

namespace CoreBankDemo.DemoRunner.Application.StateMachine;

/// <summary>
/// The one place that drives cue/topology state transitions. Terminal.Gui (and tests)
/// only ever call these methods and read <see cref="State"/>; no scenario or process
/// logic lives in the UI layer (ADR-015).
/// </summary>
public sealed class SessionController
{
    private readonly IProcessAdapter _process;
    private readonly IHttpActionExecutor _http;
    private readonly IHealthMonitor _health;
    private readonly IBrowserLauncher _browser;
    private readonly ILoadWorkflowRunner _loadWorkflow;
    private readonly IJournal _journal;
    private readonly TimeProvider _time;

    public SessionState State { get; }

    public LoadWorkflowResult? LastLoadWorkflowResult { get; private set; }

    public SessionController(
        TalkScenarioDefinition scenario,
        SessionMode mode,
        string runId,
        string sourceCommit,
        IProcessAdapter process,
        IHttpActionExecutor http,
        IHealthMonitor health,
        IBrowserLauncher browser,
        ILoadWorkflowRunner loadWorkflow,
        IJournal journal,
        TimeProvider time)
    {
        _process = process;
        _http = http;
        _health = health;
        _browser = browser;
        _loadWorkflow = loadWorkflow;
        _journal = journal;
        _time = time;

        var cues = scenario.Cues.Select(c => new CueRuntimeState(c)).ToList();
        if (cues.Count > 0)
        {
            cues[0].Status = CueStatus.Available;
        }

        State = new SessionState
        {
            RunId = runId,
            ScenarioName = scenario.Name,
            ScenarioVersion = scenario.ScenarioVersion,
            SourceCommit = sourceCommit,
            Mode = mode,
            Cues = cues,
        };
    }

    /// <summary>Detects whether a healthy, fingerprint-matching topology is already attachable.</summary>
    public Task<TopologyHandle?> DetectAttachableTopologyAsync(string profileName, CancellationToken ct) =>
        _process.TryAttachAsync(profileName, ct);

    public async Task<TopologyHandle> StartTopologyAsync(string profileName, CancellationToken ct)
    {
        var handle = await _process.StartOwnedAsync(profileName, ct);
        State.Topologies[profileName] = handle;
        return handle;
    }

    public async Task<TopologyHandle?> AttachTopologyAsync(string profileName, CancellationToken ct)
    {
        var handle = await _process.TryAttachAsync(profileName, ct);
        if (handle is not null)
        {
            State.Topologies[profileName] = handle;
        }

        return handle;
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        if (State.CurrentCue.Status == CueStatus.Running)
        {
            State.CurrentCue.Status = CueStatus.Cancelled;
            await JournalAsync(State.CurrentCue, null, ct);
        }

        foreach (var handle in State.Topologies.Values.Where(h => h.IsOwned))
        {
            await _process.StopOwnedAsync(handle, ct);
        }
    }

    /// <summary>Recovers session position from the journal's last checkpoint (e.g. after Ctrl+C or a crash).</summary>
    public async Task ResumeAsync(CancellationToken ct)
    {
        var checkpoint = await _journal.TryReadLastCheckpointAsync(State.RunId, ct);
        if (checkpoint is null)
        {
            return;
        }

        var index = State.Cues.ToList().FindIndex(c => c.Definition.Id == checkpoint.Cue);
        if (index < 0)
        {
            return;
        }

        if (checkpoint.State == CueStatus.Passed)
        {
            for (var i = 0; i <= index; i++)
            {
                State.Cues[i].Status = CueStatus.Passed;
            }

            if (index + 1 < State.Cues.Count)
            {
                State.Cues[index + 1].Status = CueStatus.Available;
                State.CurrentCueIndex = index + 1;
            }
            else
            {
                State.CurrentCueIndex = index;
            }
        }
        else
        {
            // An interrupted/ambiguous checkpoint is never upgraded to Passed from the journal alone.
            for (var i = 0; i < index; i++)
            {
                State.Cues[i].Status = CueStatus.Passed;
            }

            State.Cues[index].Status = CueStatus.Ambiguous;
            State.Cues[index].EvidenceSummary = "Recovered after interruption; reconcile before continuing.";
            State.CurrentCueIndex = index;
        }
    }

    public async Task<CueRuntimeState> PreArmCurrentAsync(CancellationToken ct)
    {
        var cue = State.CurrentCue;
        if (State.IsBusy || cue.Status is CueStatus.Running or CueStatus.Passed)
        {
            return cue;
        }

        State.IsBusy = true;
        try
        {
            var outcomes = new List<ActionOutcome>();
            foreach (var action in cue.Definition.PreArmActions)
            {
                outcomes.Add(await ExecuteActionAsync(cue, action, ct));
            }

            var gating = outcomes.Where(o => o.IsGating).ToList();
            if (gating.Count == 0 || gating.All(o => o.Success))
            {
                cue.Status = CueStatus.PreArmed;
                cue.EvidenceSummary = "Pre-armed. No payment/command has been sent.";
            }
            else
            {
                cue.Status = CueStatus.Failed;
                cue.EvidenceSummary = string.Join("; ", gating.Where(o => !o.Success).Select(o => o.Summary));
            }

            cue.LastUpdatedAt = _time.GetUtcNow();
            await JournalAsync(cue, "pre-arm", ct);
            return cue;
        }
        finally
        {
            State.IsBusy = false;
        }
    }

    public Task<CueRuntimeState> RunCurrentAsync(CancellationToken ct) => DispatchAsync(State.CurrentCue.Definition.Actions, isRetry: false, ct);

    /// <summary>
    /// Retries the current cue. A prior <see cref="CueStatus.Ambiguous"/> outcome is
    /// reconciled with only the cue's non-mutating actions (assertHttp/waitForHealth);
    /// a prior <see cref="CueStatus.Failed"/> outcome safely re-runs the full action
    /// list because every mutating action carries the same deterministic idempotency key.
    /// </summary>
    public Task<CueRuntimeState> RetryCurrentAsync(CancellationToken ct)
    {
        var cue = State.CurrentCue;
        var actions = cue.Status == CueStatus.Ambiguous
            ? cue.Definition.Actions.Where(a => !a.IsMutating).ToList()
            : cue.Definition.Actions;

        return DispatchAsync(actions, isRetry: true, ct);
    }

    public async Task<IReadOnlyList<ActionOutcome>> RunInvestigateAsync(CancellationToken ct)
    {
        var cue = State.CurrentCue;
        var outcomes = new List<ActionOutcome>();
        foreach (var action in cue.Definition.InvestigateActions)
        {
            outcomes.Add(await ExecuteActionAsync(cue, action, ct));
        }

        return outcomes;
    }

    public bool TryAdvanceToNext()
    {
        if (!State.CanAdvanceToNext)
        {
            return false;
        }

        State.CurrentCueIndex++;
        var next = State.CurrentCue;
        if (next.Status == CueStatus.Locked)
        {
            next.Status = CueStatus.Available;
        }

        return true;
    }

    private async Task<CueRuntimeState> DispatchAsync(IReadOnlyList<ScenarioActionDefinition> actions, bool isRetry, CancellationToken ct)
    {
        var cue = State.CurrentCue;

        // Fail-closed single-in-flight guard: a duplicate activation while running is ignored, not queued.
        if (State.IsBusy || cue.Status == CueStatus.Running)
        {
            return cue;
        }

        State.IsBusy = true;
        cue.Status = CueStatus.Running;
        await JournalAsync(cue, "run", ct);
        try
        {
            if (isRetry)
            {
                cue.RetryCount++;
            }

            var outcomes = new List<ActionOutcome>();
            foreach (var action in actions)
            {
                var outcome = await ExecuteActionAsync(cue, action, ct);
                outcomes.Add(outcome);
            }

            var gating = outcomes.Where(o => o.IsGating).ToList();
            if (gating.Any(o => o.IsAmbiguous))
            {
                cue.Status = CueStatus.Ambiguous;
            }
            else if (gating.Count == 0 || gating.All(o => o.Success))
            {
                cue.Status = CueStatus.Passed;
            }
            else
            {
                cue.Status = CueStatus.Failed;
            }

            cue.EvidenceSummary = string.Join("; ", outcomes.Select(o => o.Summary));
            cue.LastUpdatedAt = _time.GetUtcNow();
            await JournalAsync(cue, "assert", ct);
            return cue;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cue.Status = CueStatus.Cancelled;
            cue.EvidenceSummary = "Cue cancelled before its evidence was proven.";
            cue.LastUpdatedAt = _time.GetUtcNow();
            await JournalAsync(cue, "cancelled", CancellationToken.None);
            return cue;
        }
        catch (Exception ex)
        {
            cue.Status = CueStatus.Failed;
            cue.EvidenceSummary = $"Cue failed unexpectedly: {ex.Message}";
            cue.LastUpdatedAt = _time.GetUtcNow();
            await JournalAsync(cue, "failed", CancellationToken.None);
            throw;
        }
        finally
        {
            State.IsBusy = false;
        }
    }

    private async Task<ActionOutcome> ExecuteActionAsync(CueRuntimeState cue, ScenarioActionDefinition action, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case ActionKind.SelectTopology:
            {
                if (State.Topologies.ContainsKey(action.ProfileName!))
                {
                    return ActionOutcome.Passed($"Topology '{action.ProfileName}' already selected.");
                }

                var attached = await _process.TryAttachAsync(action.ProfileName!, ct);
                if (attached is not null)
                {
                    State.Topologies[action.ProfileName!] = attached;
                    return ActionOutcome.Passed($"Attached to existing '{action.ProfileName}' topology.");
                }

                var started = await _process.StartOwnedAsync(action.ProfileName!, ct);
                State.Topologies[action.ProfileName!] = started;
                return ActionOutcome.Passed($"Started owned '{action.ProfileName}' topology.");
            }

            case ActionKind.WaitForHealth:
            {
                var healthy = await _health.WaitForHealthyAsync(action.ResourceName!, TimeSpan.FromSeconds(action.TimeoutSeconds!.Value), ct);
                var recentOutput = State.Topologies.Values
                    .Where(handle => handle.IsOwned)
                    .Select(_process.GetRecentOutput)
                    .LastOrDefault(output => !string.IsNullOrWhiteSpace(output));
                return healthy
                    ? ActionOutcome.Passed($"{action.ResourceName} healthy.")
                    : ActionOutcome.FailedResult(
                        $"{action.ResourceName} did not become healthy within {action.TimeoutSeconds}s." +
                        (recentOutput is null ? string.Empty : $" Recent AppHost output: {recentOutput}"));
            }

            case ActionKind.SendHttp:
            {
                var idempotencyKey = action.IdempotencyKeyRef is null
                    ? null
                    : IdempotencyKeyProvider.ForCueAction(State.RunId, cue.Definition.Id, action.IdempotencyKeyRef);

                // Scenario bodies may reference the derived key via a literal placeholder
                // (data substitution only — never executable) for endpoints that key
                // idempotency off a body field rather than a header.
                var body = idempotencyKey is null
                    ? action.BodyJson
                    : action.BodyJson?.Replace("{{IDEMPOTENCY_KEY}}", idempotencyKey, StringComparison.Ordinal);

                var result = await _http.SendAsync(action.EndpointId!, action.Method!, body, idempotencyKey, ct);

                if (result.IsAmbiguous)
                {
                    return ActionOutcome.Ambiguous($"{action.EndpointId} timed out: {result.ErrorSummary}");
                }

                if (!result.IsSuccess)
                {
                    return ActionOutcome.FailedResult($"{action.EndpointId} failed ({result.StatusCode}): {result.ErrorSummary}");
                }

                if (action.CaptureAs is not null)
                {
                    var captured = JsonPathReader.TryRead(result.BodyJson, action.CaptureJsonPath);
                    if (captured is not null)
                    {
                        cue.Captures[action.CaptureAs] = captured;
                    }
                }

                return ActionOutcome.Passed($"{action.EndpointId} accepted ({result.StatusCode}).");
            }

            case ActionKind.AssertHttp when action.CaptureRefA is not null && action.CaptureRefB is not null:
            {
                cue.Captures.TryGetValue(action.CaptureRefA, out var a);
                cue.Captures.TryGetValue(action.CaptureRefB, out var b);
                var equal = a is not null && a == b;
                var expectEqual = action.ExpectEqual ?? true;
                return equal == expectEqual
                    ? ActionOutcome.Passed($"{action.CaptureRefA} == {action.CaptureRefB} ({a ?? "<missing>"}).")
                    : ActionOutcome.FailedResult($"{action.CaptureRefA} ('{a ?? "<missing>"}') vs {action.CaptureRefB} ('{b ?? "<missing>"}') did not satisfy ExpectEqual={expectEqual}.");
            }

            case ActionKind.AssertHttp:
            {
                var result = await _http.SendAsync(action.EndpointId!, "GET", null, null, ct);
                if (result.IsAmbiguous)
                {
                    return ActionOutcome.Ambiguous($"{action.EndpointId} assertion timed out: {result.ErrorSummary}");
                }

                if (!result.IsSuccess)
                {
                    return ActionOutcome.FailedResult($"{action.EndpointId} assertion call failed ({result.StatusCode}).");
                }

                var actual = JsonPathReader.TryRead(result.BodyJson, action.CaptureJsonPath);
                return string.Equals(actual, action.ExpectedValue, StringComparison.Ordinal)
                    ? ActionOutcome.Passed($"{action.EndpointId}{action.CaptureJsonPath} == '{action.ExpectedValue}'.")
                    : ActionOutcome.FailedResult($"{action.EndpointId}{action.CaptureJsonPath} was '{actual ?? "<missing>"}', expected '{action.ExpectedValue}'.");
            }

            case ActionKind.RunAcceptedLoadWorkflow:
            {
                var result = await _loadWorkflow.RunAsync(action.ExpectedUniqueCount, ct);
                LastLoadWorkflowResult = result;
                return result.AllPassed
                    ? ActionOutcome.Passed($"Load workflow passed: {result.Invariants.Count} invariant(s) all green.")
                    : ActionOutcome.FailedResult($"Load workflow failed at {result.FailedAtPhase}: {result.ErrorSummary ?? "one or more invariants failed"}.");
            }

            case ActionKind.OpenKnownUrl:
            {
                var opened = await _browser.OpenAsync(action.LinkId!, ct);
                // Broken links fail locally and never gate banking/load-test cue evidence.
                return ActionOutcome.Passed(opened ? $"Opened {action.LinkId}." : $"Could not open {action.LinkId} locally.", isGating: false);
            }

            case ActionKind.SpeakerPause:
                return ActionOutcome.Passed(action.Note ?? "Speaker pause.", isGating: false);

            default:
                return ActionOutcome.FailedResult($"Unrecognized action kind '{action.Kind}'.");
        }
    }

    private Task JournalAsync(CueRuntimeState cue, string? phase, CancellationToken ct) =>
        _journal.AppendAsync(
            new JournalEntry(
                State.RunId,
                State.ScenarioVersion,
                State.SourceCommit,
                cue.Definition.SlideAnchor,
                cue.Definition.Id,
                phase,
                cue.Status,
                _time.GetUtcNow(),
                cue.EvidenceSummary),
            ct);
}
