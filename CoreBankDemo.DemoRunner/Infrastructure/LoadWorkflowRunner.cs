using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Application.Scenarios;
using CoreBankDemo.DemoRunner.Application.StateMachine;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>
/// Thin presentation adapter over the Story 7.3 accepted LoadTestSupport/k6 workflow.
/// Reset and k6 execution are already owned by the LoadTests AppHost's one-shot
/// initializer (ADR-014) — this runner never reimplements them, only polls drain and
/// relays the single assertion endpoint's invariant results (ADR-015).
/// </summary>
public sealed class LoadWorkflowRunner(IHttpActionExecutor http, TimeProvider time) : ILoadWorkflowRunner
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>The five constraints.md invariants, in canonical order. A name absent from the
    /// LoadTestSupport response is reported as failed-and-unknown rather than silently dropped.</summary>
    private static readonly IReadOnlyList<(string CanonicalName, string[] SourceKeys)> CanonicalInvariants =
    [
        ("Exactly-once processing", ["NoDuplicateProcessing"]),
        ("Zero message loss", ["AllSubmittedProcessed"]),
        ("Balance conservation", ["BalanceConservation", "BalancesCorrect"]),
        ("Terminal-state completeness", ["NoFailedMessages", "NoPendingMessages"]),
        ("Per-key ordering", ["PerKeyOrdering", "NoDuplicateProcessing"]),
    ];

    public async Task<LoadWorkflowResult> RunAsync(int? expectedUniqueCount, CancellationToken ct)
    {
        // Run: confirm LoadTestSupport is reachable. Reset and k6 already ran as part of
        // topology selection (ADR-014's initializer) — never re-triggered from here.
        var probe = await http.SendAsync(KnownEndpoints.LoadTestSupportDrain, "GET", null, null, ct);
        if (!probe.IsSuccess)
        {
            return LoadWorkflowResult.PhaseFailure(LoadWorkflowPhase.Run, probe.ErrorSummary ?? "LoadTestSupport is not reachable.");
        }

        // Wait: poll drain until IsDrained or timeout.
        var deadline = time.GetUtcNow() + DrainTimeout;
        while (true)
        {
            var drain = await http.SendAsync(KnownEndpoints.LoadTestSupportDrain, "GET", null, null, ct);
            if (!drain.IsSuccess)
            {
                return LoadWorkflowResult.PhaseFailure(LoadWorkflowPhase.Wait, drain.ErrorSummary ?? "Drain probe failed.");
            }

            if (string.Equals(JsonPathReader.TryRead(drain.BodyJson, "$.isDrained"), "true", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (time.GetUtcNow() >= deadline)
            {
                return LoadWorkflowResult.PhaseFailure(LoadWorkflowPhase.Wait, "Timed out waiting for drain.");
            }

            try
            {
                await Task.Delay(DrainPollInterval, time, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return LoadWorkflowResult.PhaseFailure(LoadWorkflowPhase.Wait, "Cancelled while waiting for drain.");
            }
        }

        // Assert: the same, single assertion endpoint owns every invariant computation.
        var query = expectedUniqueCount is { } n
            ? new Dictionary<string, string> { ["expectedUnique"] = n.ToString() }
            : null;
        var assertResult = await http.SendAsync(KnownEndpoints.LoadTestSupportAssert, "GET", null, null, ct, query);
        if (!assertResult.IsSuccess)
        {
            return LoadWorkflowResult.PhaseFailure(LoadWorkflowPhase.Assert, assertResult.ErrorSummary ?? "Assertion call failed.");
        }

        var invariants = CanonicalInvariants
            .Select(spec => ReadInvariant(assertResult.BodyJson, spec.CanonicalName, spec.SourceKeys))
            .ToList();

        return LoadWorkflowResult.Success(invariants);
    }

    private static InvariantResult ReadInvariant(string? bodyJson, string canonicalName, string[] sourceKeys)
    {
        foreach (var key in sourceKeys)
        {
            var passedText = JsonPathReader.TryRead(bodyJson, $"$.checks.{key}.passed");
            if (passedText is null)
            {
                continue;
            }

            var detail = JsonPathReader.TryRead(bodyJson, $"$.checks.{key}.detail") ?? key;
            return new InvariantResult(canonicalName, string.Equals(passedText, "true", StringComparison.OrdinalIgnoreCase), detail);
        }

        return new InvariantResult(canonicalName, false, "Not reported by LoadTestSupport's assertion response.");
    }
}
