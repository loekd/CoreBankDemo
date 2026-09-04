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
                    "perKeyOrdering": {"passed":true,"detail":"ordered"},
                    "inlineInstantSettlement": {"passed":true,"detail":"count=20"}
                  },
                  "summary": {"inlineInstantSettlementCount":20}
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
            command.Resource == KnownResources.K6 && command.Command == ResourceCommand.Start);
        progress.Select(item => item.Phase).Should().Contain(
            [LoadWorkflowPhase.Reset, LoadWorkflowPhase.Run, LoadWorkflowPhase.Wait, LoadWorkflowPhase.Assert, LoadWorkflowPhase.Investigate, LoadWorkflowPhase.Completed]);
    }

    [Fact]
    public async Task RunAsync_ResetFailure_StopsBeforeK6()
    {
        using var client = new HttpClient(new QueueHttpHandler(
            new Queue<HttpResponseMessage>(
            [
                Json(HttpStatusCode.ServiceUnavailable, "down"),
                .. InvestigationResponses(),
            ])));
        var aspire = new FakeAspireAdapter();
        var runner = new LoadWorkflowRunner(client, aspire, TimeProvider.System);

        var result = await runner.RunAsync(100, new InlineProgress<LoadWorkflowProgress>(_ => { }), CancellationToken.None);

        result.Completed.Should().BeFalse();
        result.FinalPhase.Should().Be(LoadWorkflowPhase.Reset);
        result.InvestigationDetail.Should().Contain(KnownEndpoints.PaymentsOutbox);
        aspire.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_K6Failure_IsNotReportedAsPassed()
    {
        using var client = new HttpClient(new QueueHttpHandler(
            new Queue<HttpResponseMessage>(
            [
                Json(HttpStatusCode.OK, "{}"),
                .. InvestigationResponses(),
            ])));
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
            Json(HttpStatusCode.OK,
                """
                {
                  "allPassed":false,
                  "checks":{
                    "noDuplicateProcessing":{"passed":true,"detail":"unique"},
                    "allSubmittedProcessed":{"passed":false,"detail":"missing 1"},
                    "balanceConservation":{"passed":true,"detail":"balanced"},
                    "balancesCorrect":{"passed":true,"detail":"replay"},
                    "noFailedMessages":{"passed":true,"detail":"none"},
                    "noPendingMessages":{"passed":false,"detail":"pending 1"},
                    "perKeyOrdering":{"passed":false,"detail":"partition 2 inverted"},
                    "inlineInstantSettlement":{"passed":true,"detail":"count=3"}
                  },
                  "summary":{"inlineInstantSettlementCount":3}
                }
                """),
            .. InvestigationResponses(),
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
        result.Invariants.Should().HaveCount(5);
        result.Invariants.Single(item => item.Name == "Zero message loss").Detail.Should().Be("missing 1");
        result.Invariants.Single(item => item.Name == "Per-key ordering").Detail.Should().Contain("inverted");
        result.InlineSettlement.Count.Should().Be(3);
        result.InvestigationDetail.Should().Contain(KnownEndpoints.CoreBankInbox);
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

    [Fact]
    public async Task RunAsync_K6WithoutExecutionIdentity_RequiresObservedRunningTransition()
    {
        var responses = SuccessfulResponses();
        using var client = new HttpClient(new QueueHttpHandler(responses));
        var aspire = new FakeAspireAdapter();
        aspire.Queue(
            K6Snapshot(ResourceCondition.Completed, ""),
            K6Snapshot(ResourceCondition.Running, ""),
            K6Snapshot(ResourceCondition.Completed, ""));
        var runner = new LoadWorkflowRunner(client, aspire, TimeProvider.System);

        var result = await runner.RunAsync(100, new InlineProgress<LoadWorkflowProgress>(_ => { }), CancellationToken.None);

        result.AllPassed.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_NonObjectAssertionPaths_FailClosedWithoutThrowing()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            Json(HttpStatusCode.OK, "{}"),
            Json(HttpStatusCode.OK, """{"isDrained":true}"""),
            Json(HttpStatusCode.OK, """{"allPassed":false,"checks":"invalid","summary":[]}"""),
            .. InvestigationResponses(),
        ]);
        using var client = new HttpClient(new QueueHttpHandler(responses));
        var aspire = new FakeAspireAdapter();
        aspire.Queue(
            K6Snapshot(ResourceCondition.Completed, "old"),
            K6Snapshot(ResourceCondition.Completed, "new"));

        var result = await new LoadWorkflowRunner(client, aspire, TimeProvider.System)
            .RunAsync(100, new InlineProgress<LoadWorkflowProgress>(_ => { }), CancellationToken.None);

        result.AllPassed.Should().BeFalse();
        result.Invariants.Should().HaveCount(5);
        result.Invariants.Should().OnlyContain(item => !item.Passed);
        result.InlineSettlement.Observed.Should().BeFalse();
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

    private static IEnumerable<HttpResponseMessage> InvestigationResponses() =>
    [
        Json(HttpStatusCode.OK, "[]"),
        Json(HttpStatusCode.OK, "[]"),
        Json(HttpStatusCode.OK, "[]"),
        Json(HttpStatusCode.OK, "[]"),
    ];

    private static Queue<HttpResponseMessage> SuccessfulResponses() =>
        new(
        [
            Json(HttpStatusCode.OK, "{}"),
            Json(HttpStatusCode.OK, """{"isDrained":true}"""),
            Json(HttpStatusCode.OK,
                """
                {
                  "allPassed":true,
                  "checks":{
                    "noDuplicateProcessing":{"passed":true,"detail":"unique"},
                    "allSubmittedProcessed":{"passed":true,"detail":"all"},
                    "balanceConservation":{"passed":true,"detail":"balanced"},
                    "balancesCorrect":{"passed":true,"detail":"replay"},
                    "noFailedMessages":{"passed":true,"detail":"none"},
                    "noPendingMessages":{"passed":true,"detail":"drained"},
                    "perKeyOrdering":{"passed":true,"detail":"ordered"},
                    "inlineInstantSettlement":{"passed":true,"detail":"count=1"}
                  },
                  "summary":{"inlineInstantSettlementCount":1}
                }
                """),
            .. InvestigationResponses(),
        ]);

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
