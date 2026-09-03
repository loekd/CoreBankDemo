using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Tests.Fakes;

public sealed class OperatorHarness
{
    public FakeAspireAdapter Aspire { get; } = new();
    public FakeProcessAdapter Processes { get; } = new();
    public FakePaymentGateway Payments { get; } = new();
    public FakeLoadWorkflowRunner Load { get; } = new();
    public FakeEvidenceExporter Exporter { get; } = new();
    public FakeBrowserLauncher Browser { get; } = new();
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
            Browser,
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
            reachable && fingerprint ? null : "not verified");

    public static IReadOnlyList<ResourceSnapshot> DefaultResources(TopologyProfile profile)
    {
        var names = KnownResources.RequiredFor(profile);
        return names.Select(name => new ResourceSnapshot(
            name,
            ResourceCondition.Healthy,
            "Healthy",
            [],
            KnownResources.ExpectedReplicaCount(name))).ToList();
    }
}

public sealed class FakeAspireAdapter : IAspireAdapter
{
    private readonly Queue<TopologySnapshot> _snapshots = new();

    public IReadOnlyList<TopologySnapshot> Discovered { get; set; } = [];
    public List<(TopologyProfile Profile, string Resource, ResourceCommand Command)> Commands { get; } = [];
    public ResourceCommandResult CommandResult { get; set; } = new(true, "dispatched");
    public TopologySnapshot? DefaultSnapshot { get; set; }

    public void Queue(params TopologySnapshot[] snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            _snapshots.Enqueue(snapshot);
        }
    }

    public Task<IReadOnlyList<TopologySnapshot>> DiscoverAsync(CancellationToken ct) =>
        Task.FromResult(Discovered);

    public Task<TopologySnapshot> GetSnapshotAsync(TopologyProfile profile, CancellationToken ct)
    {
        if (_snapshots.Count > 0)
        {
            return Task.FromResult(_snapshots.Dequeue());
        }

        return Task.FromResult(DefaultSnapshot ?? OperatorHarness.Snapshot(profile));
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
    public string Output { get; set; } = "bounded output";

    public Task<TopologyHandle> StartOwnedAsync(TopologyProfile profile, CancellationToken ct)
    {
        StartCount++;
        return Task.FromResult(new TopologyHandle(profile, true, 42, "owned:42"));
    }

    public string GetRecentOutput(TopologyHandle handle) => Output;

    public Task StopOwnedAsync(TopologyHandle handle, CancellationToken ct)
    {
        if (handle.IsOwned)
        {
            StopCount++;
        }

        return Task.CompletedTask;
    }
}

public sealed class FakePaymentGateway : IPaymentGateway
{
    private readonly Queue<PaymentResult> _results = new();
    private readonly Queue<InspectionResult> _inspections = new();

    public List<PaymentSubmission> Submissions { get; } = [];
    public TaskCompletionSource? SubmissionStarted { get; set; }
    public TaskCompletionSource? ReleaseSubmission { get; set; }

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

    public Task<InspectionResult> QueryOutcomeAsync(
        TopologyProfile profile,
        string transactionIdOrKey,
        CancellationToken ct) =>
        Task.FromResult(NextInspection("outcome"));

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

public sealed class FakeLoadWorkflowRunner : ILoadWorkflowRunner
{
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

    public Task<LoadWorkflowResult> RunAsync(
        int? expectedUniqueCount,
        IProgress<LoadWorkflowProgress> progress,
        CancellationToken ct)
    {
        progress.Report(new LoadWorkflowProgress(LoadWorkflowPhase.Reset, TimeSpan.Zero, "reset"));
        progress.Report(new LoadWorkflowProgress(LoadWorkflowPhase.Completed, TimeSpan.FromSeconds(1), "done"));
        return Task.FromResult(Result);
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

public sealed class FakeBrowserLauncher : IBrowserLauncher
{
    public List<string> Opened { get; } = [];
    public List<string?> VerifiedUrls { get; } = [];

    public Task<bool> OpenAsync(string linkId, string? verifiedUrl, CancellationToken ct)
    {
        Opened.Add(linkId);
        VerifiedUrls.Add(verifiedUrl);
        return Task.FromResult(true);
    }
}
