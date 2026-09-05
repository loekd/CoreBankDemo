using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Doctor;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Infrastructure;

namespace CoreBankDemo.DemoRunner.Tests.Fakes;

public sealed class OperatorHarness
{
    public OperatorHarness()
    {
        Preflight.DiscoveryProvider = () => Aspire.Discovery;
    }

    public FakeAspireAdapter Aspire { get; } = new();
    public FakeProcessAdapter Processes { get; } = new();
    public FakePaymentGateway Payments { get; } = new();
    public FakeLoadWorkflowRunner Load { get; } = new();
    public FakeEvidenceExporter Exporter { get; } = new();
    public FakeFaultInjector Faults { get; } = new();
    public FakeBrowserLauncher Browser { get; } = new();
    public FakePreflightRunner Preflight { get; } = new();
    public FakeTimeProvider Time { get; } = new();

    public OperatorConsoleController CreateController(
        OperatorConsoleOptions? options = null,
        Func<string>? keyFactory = null) =>
        new(
            Aspire,
            Processes,
            Payments,
            Load,
            Exporter,
            Faults,
            Browser,
            Preflight,
            Time,
            options ?? new OperatorConsoleOptions
            {
                PollInterval = TimeSpan.Zero,
                TransitionTimeout = TimeSpan.FromSeconds(1),
                SnapshotFreshness = TimeSpan.FromSeconds(5),
            },
            keyFactory ?? (() => "generated-key"));

    public static TopologySnapshot Snapshot(
        TopologyProfile profile,
        DateTimeOffset? capturedAt = null,
        bool reachable = true,
        bool fingerprint = true,
        params ResourceSnapshot[] resources) =>
        new(
            profile,
            capturedAt ?? new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero),
            reachable,
            fingerprint,
            fingerprint ? $"{profile}-fingerprint" : string.Empty,
            resources.Length == 0 ? DefaultResources(profile) : resources,
            reachable && fingerprint ? null : "not verified",
            "https://localhost:17253");

    /// <summary>
    /// A snapshot that also carries a live <c>devproxy</c> resource. The fault chip refuses to
    /// claim Armed or Faults in force without one, so every armed-topology test needs it.
    /// </summary>
    public static TopologySnapshot ArmedSnapshot(
        TopologyProfile profile,
        DateTimeOffset? capturedAt = null,
        ResourceCondition devProxyCondition = ResourceCondition.Healthy) =>
        Snapshot(
            profile,
            capturedAt,
            resources: [.. DefaultResources(profile), DevProxyResource(devProxyCondition)]);

    public static ResourceSnapshot DevProxyResource(ResourceCondition condition = ResourceCondition.Healthy) =>
        new(
            KnownResources.DevProxy,
            condition,
            condition.ToString(),
            [],
            1,
            InstanceNames: [KnownResources.DevProxy],
            AllowedCommands: Enum.GetValues<ResourceCommand>().ToHashSet());

    public static IReadOnlyList<ResourceSnapshot> DefaultResources(TopologyProfile profile)
    {
        var names = KnownResources.RequiredFor(profile);
        var endpoints = KnownResources.ExpectedEndpointPorts(profile);
        return names.Select(name =>
        {
            var replicas = KnownResources.ExpectedReplicaCount(name);
            return new ResourceSnapshot(
                name,
                ResourceCondition.Healthy,
                "Healthy",
                endpoints.TryGetValue(name, out var port) ? [$"http://127.0.0.1:{port}"] : [],
                replicas,
                InstanceNames: Enumerable.Range(1, replicas).Select(index => $"{name}-{index}").ToList(),
                AllowedCommands: Enum.GetValues<ResourceCommand>().ToHashSet());
        }).ToList();
    }
}

public sealed class FakeAspireAdapter : IAspireAdapter
{
    private readonly Queue<TopologySnapshot> _snapshots = new();

    public TopologyDiscoveryResult Discovery { get; set; } = TopologyDiscoveryResult.Success([]);
    public IReadOnlyList<TopologySnapshot> Discovered
    {
        get => Discovery.Snapshots;
        set => Discovery = TopologyDiscoveryResult.Success(value);
    }
    public List<(TopologyProfile Profile, string Resource, ResourceCommand Command)> Commands { get; } = [];
    public ResourceCommandResult CommandResult { get; set; } = new(
        ResourceDispatchStatus.Dispatched,
        "dispatched",
        ["resource-1"],
        []);
    public TopologySnapshot? DefaultSnapshot { get; set; }
    public TaskCompletionSource? SnapshotStarted { get; set; }
    public TaskCompletionSource? ReleaseSnapshot { get; set; }

    public void Queue(params TopologySnapshot[] snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            _snapshots.Enqueue(snapshot);
        }
    }

    public Task<TopologyDiscoveryResult> DiscoverAsync(CancellationToken ct) =>
        Task.FromResult(Discovery);

    public async Task<TopologySnapshot> GetSnapshotAsync(TopologyProfile profile, CancellationToken ct)
    {
        SnapshotStarted?.TrySetResult();
        if (ReleaseSnapshot is not null)
        {
            await ReleaseSnapshot.Task.WaitAsync(ct);
        }

        if (_snapshots.Count > 0)
        {
            return _snapshots.Dequeue();
        }

        return DefaultSnapshot ?? OperatorHarness.Snapshot(profile);
    }

    public Task<ResourceCommandResult> ExecuteResourceCommandAsync(
        TopologyProfile profile,
        string resourceName,
        ResourceCommand command,
        CancellationToken ct)
    {
        Commands.Add((profile, resourceName, command));
        return Task.FromResult(CommandResult);
    }
}

public sealed class FakeProcessAdapter : IProcessAdapter
{
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int ForgetCount { get; private set; }
    public string Output { get; set; } = "bounded output";
    public TaskCompletionSource? StopStarted { get; set; }
    public TaskCompletionSource? ReleaseStop { get; set; }
    public Exception? StartException { get; set; }
    public Exception? StopException { get; set; }
    public Exception? ForgetException { get; set; }

    public List<bool> ArmFaultsRequests { get; } = [];

    public Task<TopologyHandle> StartOwnedAsync(TopologyProfile profile, bool armFaults, CancellationToken ct)
    {
        StartCount++;
        ArmFaultsRequests.Add(armFaults);
        if (StartException is not null)
        {
            throw StartException;
        }
        return Task.FromResult(new TopologyHandle(profile, true, 42, "owned:42", $"/repo/{profile}.csproj"));
    }

    public string GetRecentOutput(TopologyHandle handle) => Output;

    public async Task<OwnedStopResult> StopOwnedAsync(TopologyHandle handle, CancellationToken ct)
    {
        if (handle.IsOwned)
        {
            StopCount++;
        }

        StopStarted?.TrySetResult();
        if (ReleaseStop is not null)
        {
            await ReleaseStop.Task.WaitAsync(ct);
        }

        if (StopException is not null)
        {
            throw StopException;
        }

        return new OwnedStopResult(false, "stopped");
    }

    public Task ForgetExitedOwnedAsync(TopologyHandle handle, CancellationToken ct)
    {
        ForgetCount++;
        if (ForgetException is not null)
        {
            throw ForgetException;
        }

        return Task.CompletedTask;
    }
}

public sealed class FakePaymentGateway : IPaymentGateway
{
    private readonly Queue<PaymentResult> _results = new();
    private readonly Queue<InspectionResult> _inspections = new();

    public List<PaymentSubmission> Submissions { get; } = [];
    public List<TopologyProfile> SubmissionProfiles { get; } = [];
    public List<TopologyProfile> QueryProfiles { get; } = [];
    public TaskCompletionSource? SubmissionStarted { get; set; }
    public TaskCompletionSource? ReleaseSubmission { get; set; }
    public TaskCompletionSource? QueryStarted { get; set; }
    public TaskCompletionSource? ReleaseQuery { get; set; }

    public void Queue(params PaymentResult[] results)
    {
        foreach (var result in results)
        {
            _results.Enqueue(result);
        }
    }

    public void QueueInspections(params InspectionResult[] results)
    {
        foreach (var result in results)
        {
            _inspections.Enqueue(result);
        }
    }

    public async Task<PaymentResult> SubmitAsync(
        TopologyProfile profile,
        PaymentSubmission submission,
        CancellationToken ct)
    {
        Submissions.Add(submission);
        SubmissionProfiles.Add(profile);
        SubmissionStarted?.TrySetResult();
        if (ReleaseSubmission is not null)
        {
            await ReleaseSubmission.Task.WaitAsync(ct);
        }

        if (_results.Count > 0)
        {
            return _results.Dequeue();
        }

        return new PaymentResult(
            submission.Request.Rail == PaymentRail.Standard ? PaymentOutcome.Pending : PaymentOutcome.Completed,
            submission.Request.Rail == PaymentRail.Standard ? 202 : 200,
            "payment-id",
            submission.IdempotencyKey,
            submission.Request.Rail == PaymentRail.Standard ? "Pending" : "Completed",
            "{}",
            null,
            TimeSpan.FromMilliseconds(5));
    }

    public async Task<InspectionResult> QueryOutcomeAsync(
        TopologyProfile profile,
        string transactionIdOrKey,
        CancellationToken ct)
    {
        QueryProfiles.Add(profile);
        QueryStarted?.TrySetResult();
        if (ReleaseQuery is not null)
        {
            await ReleaseQuery.Task.WaitAsync(ct);
        }

        return NextInspection("outcome");
    }

    public Task<InspectionResult> InspectAsync(
        TopologyProfile profile,
        string endpointId,
        CancellationToken ct) =>
        Task.FromResult(NextInspection(endpointId));

    private InspectionResult NextInspection(string target) =>
        _inspections.Count > 0
            ? _inspections.Dequeue()
            : new InspectionResult(true, 200, target, "{}", null, TimeSpan.FromMilliseconds(3));
}

public sealed class FakePreflightRunner : IPreflightRunner
{
    private DoctorReport? _report;
    public Func<TopologyDiscoveryResult>? DiscoveryProvider { get; set; }
    public DoctorReport Report
    {
        get => _report ?? BuildFromDiscovery();
        set => _report = value;
    }
    public int Calls { get; private set; }
    public List<bool> ArmingRequests { get; } = [];

    public Task<DoctorReport> RunAsync(bool faultArmingRequested, CancellationToken ct)
    {
        Calls++;
        ArmingRequests.Add(faultArmingRequested);
        return Task.FromResult(faultArmingRequested && !DevProxyAvailable
            ? Report with { EnvironmentReady = false, DevProxyAvailable = false }
            : Report);
    }

    /// <summary>Mirrors the real runner: a missing binary only blocks when arming is requested.</summary>
    public bool DevProxyAvailable { get; set; } = true;

    public static DoctorReport ReadyReport(
        TopologySnapshot? regular = null,
        TopologySnapshot? loadTests = null,
        bool discoveryReachable = true)
    {
        var checks = new List<DoctorCheckResult>
        {
            DoctorCheckResult.Ok(".NET SDK available"),
            DoctorCheckResult.Ok("Aspire CLI available"),
            DoctorCheckResult.Ok("Container runtime available"),
        };
        if (!discoveryReachable)
        {
            checks.Add(DoctorCheckResult.Fail("Aspire discovery", "unreachable"));
        }

        return new DoctorReport(checks)
        {
            EnvironmentReady = true,
            DiscoveryReachable = discoveryReachable,
            Profiles = new Dictionary<TopologyProfile, ProfilePreflightResult>
            {
                [TopologyProfile.Regular] = Profile(TopologyProfile.Regular, regular, discoveryReachable),
                [TopologyProfile.LoadTests] = Profile(TopologyProfile.LoadTests, loadTests, discoveryReachable),
            },
        };
    }

    private static ProfilePreflightResult Profile(
        TopologyProfile profile,
        TopologySnapshot? snapshot,
        bool discoveryReachable) =>
        new(
            profile,
            PortsFree: snapshot is null,
            snapshot,
            CanStart: discoveryReachable && snapshot is null,
            CanAttach: discoveryReachable && snapshot?.IsReady == true,
            Detail: snapshot is null ? "not running" : "running");

    private DoctorReport BuildFromDiscovery()
    {
        var discovery = DiscoveryProvider?.Invoke() ?? TopologyDiscoveryResult.Success([]);
        var regular = discovery.Snapshots.FirstOrDefault(item => item.Profile == TopologyProfile.Regular);
        var load = discovery.Snapshots.FirstOrDefault(item => item.Profile == TopologyProfile.LoadTests);
        return ReadyReport(regular, load, discovery.IsReachable);
    }
}

public sealed class FakeLoadWorkflowRunner : ILoadWorkflowRunner
{
    public TaskCompletionSource? RunStarted { get; set; }
    public TaskCompletionSource? ReleaseRun { get; set; }
    public LoadWorkflowResult Result { get; set; } = LoadWorkflowResult.Success(
        [
            new InvariantResult("Exactly-once processing", true, "passed"),
            new InvariantResult("Zero message loss", true, "passed"),
            new InvariantResult("Balance conservation", true, "passed"),
            new InvariantResult("Terminal-state completeness", true, "passed"),
            new InvariantResult("Per-key ordering", true, "passed"),
        ],
        new InlineSettlementResult(true, "observed"),
        "details");

    public async Task<LoadWorkflowResult> RunAsync(
        int? expectedUniqueCount,
        IProgress<LoadWorkflowProgress> progress,
        CancellationToken ct)
    {
        RunStarted?.TrySetResult();
        progress.Report(new LoadWorkflowProgress(LoadWorkflowPhase.Reset, TimeSpan.Zero, "reset"));
        if (ReleaseRun is not null)
        {
            await ReleaseRun.Task.WaitAsync(ct);
        }
        progress.Report(new LoadWorkflowProgress(LoadWorkflowPhase.Completed, TimeSpan.FromSeconds(1), "done"));
        return Result;
    }
}

public sealed class FakeEvidenceExporter : IEvidenceExporter
{
    public IReadOnlyList<EvidenceRecord> Exported { get; private set; } = [];
    public EvidenceExportResult Result { get; set; } = new(true, "evidence.json", null);

    public Task<EvidenceExportResult> ExportAsync(IReadOnlyList<EvidenceRecord> records, CancellationToken ct)
    {
        Exported = records;
        return Task.FromResult(Result);
    }
}

public sealed class FakeFaultInjector : IFaultInjector
{
    public List<(TopologyProfile Profile, FaultLevels Levels)> Writes { get; } = [];
    public List<TopologyProfile> Resets { get; } = [];
    public List<TopologyProfile> Deletes { get; } = [];
    public bool DeleteSucceeds { get; set; } = true;
    public string DeleteError { get; set; } = "the file is locked";
    public List<TopologyProfile> Reads { get; } = [];

    /// <summary>Tracks what the last write/reset left on disk. Defaults to quiet.</summary>
    public FaultLevels Levels { get; set; } = FaultLevels.AllZero;

    /// <summary>
    /// Forces what <see cref="ReadAsync"/> reports, standing in for a config this console
    /// did not write — a topology already running when the console launched.
    /// </summary>
    public FaultLevels? ReadOverride { get; set; }
    public bool ReadSucceeds { get; set; } = true;
    public bool FromGeneratedSession { get; set; }
    public string? ReadError { get; set; }
    public bool WriteSucceeds { get; set; } = true;
    public string WriteError { get; set; } = "disk is read-only";
    public string Path { get; set; } = "generated/devproxyrc.session.json";

    public Task<FaultConfigReadResult> ReadAsync(TopologyProfile profile, CancellationToken ct)
    {
        Reads.Add(profile);
        return Task.FromResult(new FaultConfigReadResult(
            ReadSucceeds,
            ReadOverride ?? Levels,
            FromGeneratedSession,
            Path,
            ReadSucceeds ? null : ReadError ?? "unreadable"));
    }

    public TaskCompletionSource? WriteStarted { get; set; }
    public TaskCompletionSource? ReleaseWrite { get; set; }

    /// <summary>Highest number of writes seen in flight at once. Must never exceed one.</summary>
    public int MaxConcurrentWrites { get; private set; }

    private int _writesInFlight;

    public async Task<FaultConfigWriteResult> WriteAsync(TopologyProfile profile, FaultLevels levels, CancellationToken ct)
    {
        var inFlight = Interlocked.Increment(ref _writesInFlight);
        MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, inFlight);
        try
        {
            WriteStarted?.TrySetResult();
            if (ReleaseWrite is not null)
            {
                await ReleaseWrite.Task.WaitAsync(ct);
            }

            if (!WriteSucceeds)
            {
                return new FaultConfigWriteResult(false, Path, WriteError);
            }

            Writes.Add((profile, levels));
            Levels = levels;
            return new FaultConfigWriteResult(true, Path, null);
        }
        finally
        {
            Interlocked.Decrement(ref _writesInFlight);
        }
    }

    public Task<FaultConfigWriteResult> ResetAsync(TopologyProfile profile, CancellationToken ct)
    {
        Resets.Add(profile);
        Levels = FaultLevels.AllZero;
        return Task.FromResult(new FaultConfigWriteResult(true, Path, null));
    }

    public Task<FaultConfigWriteResult> DeleteAsync(TopologyProfile profile, CancellationToken ct)
    {
        Deletes.Add(profile);
        return Task.FromResult(DeleteSucceeds
            ? new FaultConfigWriteResult(true, Path, null)
            : new FaultConfigWriteResult(false, Path, DeleteError));
    }
}

public sealed class FakeBrowserLauncher : IBrowserLauncher
{
    public List<string> Opened { get; } = [];
    public List<string?> VerifiedUrls { get; } = [];
    public bool NextSucceeds { get; set; } = true;

    public Task<LinkOpenResult> OpenAsync(string linkId, string? verifiedUrl, CancellationToken ct)
    {
        Opened.Add(linkId);
        VerifiedUrls.Add(verifiedUrl);

        // Mirrors the one business rule the real BrowserLauncher enforces before
        // ever attempting an OS-level open: Aspire's dashboard link needs an
        // already-verified URL, since a live topology may not have one yet.
        if (linkId == KnownLinks.AspireDashboard && verifiedUrl is null)
        {
            return Task.FromResult(new LinkOpenResult(false, null));
        }

        return Task.FromResult(new LinkOpenResult(NextSucceeds, verifiedUrl ?? EndpointResolver.LinkFor(linkId)));
    }
}
