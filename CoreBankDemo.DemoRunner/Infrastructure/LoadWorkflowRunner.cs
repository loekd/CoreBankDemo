using System.Text.Json;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

public sealed class LoadWorkflowRunner(
    HttpClient httpClient,
    IAspireAdapter aspire,
    TimeProvider time) : ILoadWorkflowRunner
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan K6Timeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private static readonly IReadOnlyList<(string Name, string[] Keys)> CanonicalInvariants =
    [
        ("Exactly-once processing", ["NoDuplicateProcessing"]),
        ("Zero message loss", ["AllSubmittedProcessed"]),
        ("Balance conservation", ["BalanceConservation", "BalancesCorrect"]),
        ("Terminal-state completeness", ["NoFailedMessages", "NoPendingMessages"]),
        ("Per-key ordering", ["PerKeyOrdering"]),
    ];

    public async Task<LoadWorkflowResult> RunAsync(
        int? expectedUniqueCount,
        IProgress<LoadWorkflowProgress> progress,
        CancellationToken ct)
    {
        var startedAt = time.GetUtcNow();

        async Task<LoadWorkflowResult> FailAsync(
            LoadWorkflowPhase phase,
            string error,
            string? assertionBody = null,
            IReadOnlyList<InvariantResult>? invariants = null,
            InlineSettlementResult? inline = null)
        {
            progress.Report(new LoadWorkflowProgress(
                LoadWorkflowPhase.Investigate,
                time.GetUtcNow() - startedAt,
                $"Investigating failure from {phase}."));
            var investigation = await BuildInvestigationAsync(assertionBody, ct);
            progress.Report(new LoadWorkflowProgress(
                LoadWorkflowPhase.Failed,
                time.GetUtcNow() - startedAt,
                error));
            return LoadWorkflowResult.Failure(phase, error, invariants, investigation, inline);
        }

        progress.Report(new LoadWorkflowProgress(LoadWorkflowPhase.Reset, TimeSpan.Zero, "Resetting disposable LoadTests state."));
        var reset = await SendAsync(KnownEndpoints.LoadReset, HttpMethod.Post, null, ct);
        if (!reset.Succeeded)
        {
            return await FailAsync(LoadWorkflowPhase.Reset, reset.ErrorSummary ?? "Reset failed.");
        }

        progress.Report(new LoadWorkflowProgress(LoadWorkflowPhase.Run, time.GetUtcNow() - startedAt, "Starting the accepted k6 resource."));
        var beforeRun = await aspire.GetSnapshotAsync(TopologyProfile.LoadTests, ct);
        var k6BeforeRun = beforeRun.FindResource(KnownResources.K6);
        var priorExecutionIdentity = k6BeforeRun?.ExecutionIdentity;
        var runCommand = k6BeforeRun?.Supports(ResourceCommand.Start) == true
            ? ResourceCommand.Start
            : ResourceCommand.Restart;
        var dispatch = await aspire.ExecuteResourceCommandAsync(
            TopologyProfile.LoadTests,
            KnownResources.K6,
            runCommand,
            ct);
        if (!dispatch.Dispatched)
        {
            return await FailAsync(LoadWorkflowPhase.Run, dispatch.Detail);
        }

        var k6 = await WaitForK6Async(startedAt, priorExecutionIdentity, progress, ct);
        if (!k6.Succeeded)
        {
            return await FailAsync(LoadWorkflowPhase.Run, k6.ErrorSummary ?? "k6 did not finish successfully.");
        }

        progress.Report(new LoadWorkflowProgress(LoadWorkflowPhase.Wait, time.GetUtcNow() - startedAt, "Waiting for all four stores to drain."));
        var drainDeadline = time.GetUtcNow() + DrainTimeout;
        while (time.GetUtcNow() <= drainDeadline)
        {
            var drain = await SendAsync(KnownEndpoints.LoadDrain, HttpMethod.Get, null, ct);
            if (!drain.Succeeded)
            {
                return await FailAsync(LoadWorkflowPhase.Wait, drain.ErrorSummary ?? "Drain probe failed.");
            }

            if (ReadBoolean(drain.Body, "isDrained") == true)
            {
                break;
            }

            await Task.Delay(PollInterval, time, ct);
            progress.Report(new LoadWorkflowProgress(LoadWorkflowPhase.Wait, time.GetUtcNow() - startedAt, "Still draining; no result inferred from elapsed time."));
        }

        if (time.GetUtcNow() > drainDeadline)
        {
            return await FailAsync(LoadWorkflowPhase.Wait, "Timed out waiting for all four message stores to drain.");
        }

        progress.Report(new LoadWorkflowProgress(LoadWorkflowPhase.Assert, time.GetUtcNow() - startedAt, "Reading LoadTestSupport assertion authority."));
        var query = expectedUniqueCount is { } count
            ? new Dictionary<string, string> { ["expectedUnique"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            : null;
        var assertion = await SendAsync(KnownEndpoints.LoadAssert, HttpMethod.Get, query, ct);
        if (!assertion.Succeeded)
        {
            return await FailAsync(LoadWorkflowPhase.Assert, assertion.ErrorSummary ?? "Assertion request failed.", assertion.Body);
        }

        var invariants = CanonicalInvariants
            .Select(definition => ReadInvariant(assertion.Body, definition.Name, definition.Keys))
            .ToList();
        var inline = ReadInlineSettlement(assertion.Body);
        var authoritativePass = ReadBoolean(assertion.Body, "allPassed") == true;

        progress.Report(new LoadWorkflowProgress(LoadWorkflowPhase.Investigate, time.GetUtcNow() - startedAt, "Capturing the accepted harness source detail."));
        var investigation = await BuildInvestigationAsync(assertion.Body, ct);
        if (!authoritativePass)
        {
            progress.Report(new LoadWorkflowProgress(
                LoadWorkflowPhase.Failed,
                time.GetUtcNow() - startedAt,
                "LoadTestSupport reported allPassed=false."));
            return LoadWorkflowResult.Failure(
                LoadWorkflowPhase.Assert,
                "LoadTestSupport reported allPassed=false.",
                invariants,
                investigation,
                inline);
        }

        var result = LoadWorkflowResult.Success(invariants, inline, investigation);
        progress.Report(new LoadWorkflowProgress(
            result.AllPassed ? LoadWorkflowPhase.Completed : LoadWorkflowPhase.Failed,
            time.GetUtcNow() - startedAt,
            result.AllPassed ? "All accepted assertions passed." : "One or more accepted assertions failed."));
        return result;
    }

    private async Task<InspectionResult> WaitForK6Async(
        DateTimeOffset startedAt,
        string? priorExecutionIdentity,
        IProgress<LoadWorkflowProgress> progress,
        CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + K6Timeout;
        var sawNewExecutionState = false;
        while (time.GetUtcNow() <= deadline)
        {
            var snapshot = await aspire.GetSnapshotAsync(TopologyProfile.LoadTests, ct);
            if (!snapshot.IsReachable || !snapshot.IsFingerprintMatch)
            {
                return new InspectionResult(false, 0, KnownResources.K6, null, snapshot.ErrorSummary ?? "LoadTests fingerprint was lost.", time.GetUtcNow() - startedAt);
            }

            var resource = snapshot.FindResource(KnownResources.K6);
            if (resource?.Condition == ResourceCondition.Failed)
            {
                return new InspectionResult(false, 0, KnownResources.K6, null, resource.Detail ?? "k6 failed.", time.GetUtcNow() - startedAt);
            }

            if (resource?.Condition is ResourceCondition.Starting or ResourceCondition.Running)
            {
                sawNewExecutionState = true;
            }

            var identityChanged = !string.IsNullOrWhiteSpace(resource?.ExecutionIdentity)
                && !string.Equals(resource.ExecutionIdentity, priorExecutionIdentity, StringComparison.Ordinal);
            if (resource?.Condition == ResourceCondition.Completed
                && (identityChanged || sawNewExecutionState))
            {
                return new InspectionResult(true, 0, KnownResources.K6, resource.Detail, null, time.GetUtcNow() - startedAt);
            }

            progress.Report(new LoadWorkflowProgress(LoadWorkflowPhase.Run, time.GetUtcNow() - startedAt, "k6 is running; waiting for Aspire to report completion."));
            await Task.Delay(PollInterval, time, ct);
        }

        return new InspectionResult(false, 0, KnownResources.K6, null, "Timed out waiting for a distinguishable k6 execution to complete.", time.GetUtcNow() - startedAt);
    }

    private async Task<string> BuildInvestigationAsync(string? assertionBody, CancellationToken ct)
    {
        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(assertionBody))
        {
            sections.Add($"ASSERT RESULTS{Environment.NewLine}{assertionBody}");
        }

        foreach (var endpoint in new[]
                 {
                     KnownEndpoints.PaymentsOutbox,
                     KnownEndpoints.CoreBankInbox,
                     KnownEndpoints.CoreBankOutbox,
                     KnownEndpoints.PaymentsInbox,
                 })
        {
            var result = await SendAsync(endpoint, HttpMethod.Get, null, ct);
            sections.Add($"{endpoint}{Environment.NewLine}{result.Body ?? result.ErrorSummary}");
        }

        return JournalRedaction.Apply(string.Join(Environment.NewLine + Environment.NewLine, sections));
    }

    private async Task<InspectionResult> SendAsync(
        string endpointId,
        HttpMethod method,
        IReadOnlyDictionary<string, string>? query,
        CancellationToken ct)
    {
        var (url, _) = EndpointResolver.EndpointFor(TopologyProfile.LoadTests, endpointId);
        if (query is { Count: > 0 })
        {
            url += "?" + string.Join(
                "&",
                query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        }

        var startedAt = time.GetUtcNow();
        try
        {
            using var response = await httpClient.SendAsync(new HttpRequestMessage(method, url), ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return new InspectionResult(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                endpointId,
                body,
                response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}",
                time.GetUtcNow() - startedAt);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new InspectionResult(false, 0, endpointId, null, ex.Message, time.GetUtcNow() - startedAt);
        }
    }

    private static InvariantResult ReadInvariant(string? json, string name, IReadOnlyList<string> sourceKeys)
    {
        if (name == "Balance conservation")
        {
            return CombineChecks(json, name, ["BalanceConservation", "BalancesCorrect"]);
        }

        if (name == "Terminal-state completeness")
        {
            return CombineChecks(json, name, ["NoFailedMessages", "NoPendingMessages"]);
        }

        foreach (var key in sourceKeys)
        {
            var passed = ReadBoolean(json, "checks", key, "passed");
            if (passed is null)
            {
                continue;
            }

            return new InvariantResult(name, passed.Value, ReadString(json, "checks", key, "detail") ?? key);
        }

        return new InvariantResult(name, false, "Not reported by LoadTestSupport.");
    }

    private static InlineSettlementResult ReadInlineSettlement(string? json)
    {
        var passed = ReadBoolean(json, "checks", "inlineInstantSettlement", "passed") == true;
        var detail = ReadString(json, "checks", "inlineInstantSettlement", "detail")
            ?? "Not reported by LoadTestSupport.";
        var count = ReadInt32(json, "summary", "inlineInstantSettlementCount") ?? 0;
        return new InlineSettlementResult(passed && count > 0, detail, count, passed);
    }

    private static InvariantResult CombineChecks(string? json, string name, IReadOnlyList<string> keys)
    {
        var checks = keys.Select(key => new
        {
            Key = key,
            Passed = ReadBoolean(json, "checks", key, "passed"),
            Detail = ReadString(json, "checks", key, "detail") ?? key,
        }).ToList();
        if (checks.Any(check => check.Passed is null))
        {
            return new InvariantResult(name, false, $"Missing source check: {string.Join(", ", checks.Where(check => check.Passed is null).Select(check => check.Key))}.");
        }

        return new InvariantResult(
            name,
            checks.All(check => check.Passed == true),
            string.Join(" | ", checks.Select(check => check.Detail)));
    }

    private static bool? ReadBoolean(string? json, params string[] path)
    {
        var value = Navigate(json, path);
        return value is { ValueKind: JsonValueKind.True } ? true
            : value is { ValueKind: JsonValueKind.False } ? false
            : null;
    }

    private static int? ReadInt32(string? json, params string[] path)
    {
        var value = Navigate(json, path);
        return value is { ValueKind: JsonValueKind.Number } && value.Value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static string? ReadString(string? json, params string[] path)
    {
        var value = Navigate(json, path);
        return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : value?.ToString();
    }

    private static JsonElement? Navigate(string? json, IReadOnlyList<string> path)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var current = document.RootElement;
            foreach (var segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var found = false;
                foreach (var property in current.EnumerateObject())
                {
                    if (!string.Equals(property.Name, segment, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    current = property.Value;
                    found = true;
                    break;
                }

                if (!found)
                {
                    return null;
                }
            }

            return current.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
