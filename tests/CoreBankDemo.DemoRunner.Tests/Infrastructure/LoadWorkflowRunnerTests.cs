using System.Net;
using System.Text;
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Infrastructure;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

public class LoadWorkflowRunnerTests
{
    [Fact]
    public async Task RunAsync_ExecutesAcceptedFivePhaseWorkflowAndReturnsSixProofs()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            Json(HttpStatusCode.OK, """{"message":"reset"}"""),
            Json(HttpStatusCode.OK, """{"isDrained":true}"""),
            Json(HttpStatusCode.OK,
                """
                {
                  "allPassed": true,
                  "checks": {
                    "noDuplicateProcessing": {"passed":true,"detail":"unique"},
                    "allSubmittedProcessed": {"passed":true,"detail":"all"},
                    "balanceConservation": {"passed":true,"detail":"balanced"},
                    "balancesCorrect": {"passed":true,"detail":"replay matched"},
                    "noFailedMessages": {"passed":true,"detail":"none"},
                    "noPendingMessages": {"passed":true,"detail":"drained"},
                    "perKeyOrdering": {"passed":true,"detail":"ordered"}
                  }
                }
                """),
            Json(HttpStatusCode.OK, "[]"),
            Json(HttpStatusCode.OK, "[]"),
            Json(HttpStatusCode.OK, "[]"),
            Json(HttpStatusCode.OK, "[]"),
        ]);
        using var client = new HttpClient(new QueueHttpHandler(responses));
        var aspire = new FakeAspireAdapter();
        aspire.Queue(
            K6Snapshot(ResourceCondition.Completed, "old-run"),
            K6Snapshot(ResourceCondition.Running, "new-run"),
            K6Snapshot(ResourceCondition.Completed, "new-run"));
        var progress = new List<LoadWorkflowProgress>();
        var runner = new LoadWorkflowRunner(client, aspire, TimeProvider.System);

        var result = await runner.RunAsync(100, new InlineProgress<LoadWorkflowProgress>(progress.Add), CancellationToken.None);

        result.AllPassed.Should().BeTrue();
        result.Invariants.Should().HaveCount(5);
        result.InlineSettlement.Observed.Should().BeTrue();
        result.InvestigationDetail.Should().Contain("ASSERT RESULTS").And.Contain(KnownEndpoints.PaymentsOutbox);
        aspire.Commands.Should().ContainSingle(command =>
            command.Resource == KnownResources.K6 && command.Command == ResourceCommand.Restart);
        progress.Select(item => item.Phase).Should().Contain(
            [LoadWorkflowPhase.Reset, LoadWorkflowPhase.Run, LoadWorkflowPhase.Wait, LoadWorkflowPhase.Assert, LoadWorkflowPhase.Investigate, LoadWorkflowPhase.Completed]);
    }

    [Fact]
    public async Task RunAsync_ResetFailure_StopsBeforeK6()
    {
        using var client = new HttpClient(new QueueHttpHandler(
            new Queue<HttpResponseMessage>([Json(HttpStatusCode.ServiceUnavailable, "down")])));
        var aspire = new FakeAspireAdapter();
        var runner = new LoadWorkflowRunner(client, aspire, TimeProvider.System);

        var result = await runner.RunAsync(100, new InlineProgress<LoadWorkflowProgress>(_ => { }), CancellationToken.None);

        result.Completed.Should().BeFalse();
        result.FinalPhase.Should().Be(LoadWorkflowPhase.Reset);
        aspire.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_K6Failure_IsNotReportedAsPassed()
    {
        using var client = new HttpClient(new QueueHttpHandler(
            new Queue<HttpResponseMessage>([Json(HttpStatusCode.OK, "{}")])));
        var aspire = new FakeAspireAdapter();
        aspire.Queue(
            K6Snapshot(ResourceCondition.Completed, "old-run"),
            K6Snapshot(ResourceCondition.Failed, "new-run", "threshold failed"));
        var runner = new LoadWorkflowRunner(client, aspire, TimeProvider.System);

        var result = await runner.RunAsync(null, new InlineProgress<LoadWorkflowProgress>(_ => { }), CancellationToken.None);

        result.AllPassed.Should().BeFalse();
        result.FinalPhase.Should().Be(LoadWorkflowPhase.Run);
        result.ErrorSummary.Should().Contain("threshold");
    }

    [Fact]
    public async Task RunAsync_AuthoritativeAllPassedFalse_FailsEvenWhenDisplayedChecksPass()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            Json(HttpStatusCode.OK, "{}"),
            Json(HttpStatusCode.OK, """{"isDrained":true}"""),
            Json(HttpStatusCode.OK, """{"allPassed":false,"checks":{}}"""),
        ]);
        using var client = new HttpClient(new QueueHttpHandler(responses));
        var aspire = new FakeAspireAdapter();
        aspire.Queue(
            K6Snapshot(ResourceCondition.Completed, "old-run"),
            K6Snapshot(ResourceCondition.Completed, "new-run"));
        var runner = new LoadWorkflowRunner(client, aspire, TimeProvider.System);

        var result = await runner.RunAsync(100, new InlineProgress<LoadWorkflowProgress>(_ => { }), CancellationToken.None);

        result.AllPassed.Should().BeFalse();
        result.ErrorSummary.Should().Contain("allPassed=false");
    }

    [Fact]
    public async Task RunAsync_MissingOrderingEvidence_IsShownAsUnprovenNotPassed()
    {
        var checks =
            """
            {
              "allPassed": true,
              "checks": {
                "noDuplicateProcessing": {"passed":true,"detail":"unique"},
                "allSubmittedProcessed": {"passed":true,"detail":"all"},
                "balanceConservation": {"passed":true,"detail":"balanced"},
                "balancesCorrect": {"passed":true,"detail":"replay"},
                "noFailedMessages": {"passed":true,"detail":"none"},
                "noPendingMessages": {"passed":true,"detail":"drained"}
              }
            }
            """;
        var responses = new Queue<HttpResponseMessage>(
        [
            Json(HttpStatusCode.OK, "{}"),
            Json(HttpStatusCode.OK, """{"isDrained":true}"""),
            Json(HttpStatusCode.OK, checks),
            Json(HttpStatusCode.OK, "[]"),
            Json(HttpStatusCode.OK, "[]"),
            Json(HttpStatusCode.OK, "[]"),
            Json(HttpStatusCode.OK, "[]"),
        ]);
        using var client = new HttpClient(new QueueHttpHandler(responses));
        var aspire = new FakeAspireAdapter();
        aspire.Queue(
            K6Snapshot(ResourceCondition.Completed, "old-run"),
            K6Snapshot(ResourceCondition.Completed, "new-run"));
        var runner = new LoadWorkflowRunner(client, aspire, TimeProvider.System);

        var result = await runner.RunAsync(100, new InlineProgress<LoadWorkflowProgress>(_ => { }), CancellationToken.None);

        result.AllPassed.Should().BeFalse();
        result.Invariants.Single(invariant => invariant.Name == "Per-key ordering").Detail
            .Should().Contain("Not reported");
    }

    private static TopologySnapshot K6Snapshot(
        ResourceCondition condition,
        string executionIdentity,
        string? detail = null) =>
        OperatorHarness.Snapshot(
            TopologyProfile.LoadTests,
            resources:
            [
                new ResourceSnapshot(KnownResources.K6, condition, condition.ToString(), [], Detail: detail, ExecutionIdentity: executionIdentity),
                .. OperatorHarness.DefaultResources(TopologyProfile.LoadTests).Where(resource => resource.Name != KnownResources.K6),
            ]);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class QueueHttpHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responses.Dequeue());
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
