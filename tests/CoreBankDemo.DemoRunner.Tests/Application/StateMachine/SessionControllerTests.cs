using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Application.Scenarios;
using CoreBankDemo.DemoRunner.Application.StateMachine;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Moq;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application.StateMachine;

public class SessionControllerTests
{
    [Fact]
    public void Constructor_OnlyFirstCueIsAvailable()
    {
        var harness = new SessionControllerHarness();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a"), TestScenarios.SimpleCue("b"));
        var controller = harness.Build(scenario);

        controller.State.Cues[0].Status.Should().Be(CueStatus.Available);
        controller.State.Cues[1].Status.Should().Be(CueStatus.Locked);
        controller.State.CurrentCueIndex.Should().Be(0);
    }

    [Fact]
    public async Task RunCurrentAsync_AllActionsSucceed_MarksCuePassed()
    {
        var harness = new SessionControllerHarness();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.PaymentsSubmit, Method = "POST" },
        ]));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Passed);
        controller.State.CanAdvanceToNext.Should().BeFalse("this is the only cue");
    }

    [Fact]
    public async Task RunCurrentAsync_HttpActionFails_MarksCueFailedAndNeverAdvances()
    {
        var harness = new SessionControllerHarness();
        harness.Http.Setup(h => h.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(HttpActionResult.Error(500, "boom"));
        var scenario = TestScenarios.Build(
            TestScenarios.SimpleCue("a", actions: [new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.PaymentsSubmit, Method = "POST" }]),
            TestScenarios.SimpleCue("b"));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Failed);
        controller.TryAdvanceToNext().Should().BeFalse();
        controller.State.CurrentCueIndex.Should().Be(0);
    }

    [Fact]
    public async Task RunCurrentAsync_HttpTimesOut_MarksCueAmbiguousNotFailed()
    {
        var harness = new SessionControllerHarness();
        harness.Http.Setup(h => h.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(HttpActionResult.Timeout("no response"));
        var scenario = TestScenarios.Build(
            TestScenarios.SimpleCue("a", actions: [new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.PaymentsSubmit, Method = "POST" }]));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Ambiguous);
    }

    [Fact]
    public async Task RunCurrentAsync_ActionIsCancelled_MarksCueCancelledAndKeepsNextLocked()
    {
        var harness = new SessionControllerHarness();
        harness.Health
            .Setup(h => h.WaitForHealthyAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var scenario = TestScenarios.Build(
            TestScenarios.SimpleCue("a", actions:
            [
                new ScenarioActionDefinition
                {
                    Kind = ActionKind.WaitForHealth,
                    ResourceName = KnownResources.CoreBankApi,
                    TimeoutSeconds = 1,
                },
            ]),
            TestScenarios.SimpleCue("b"));
        var controller = harness.Build(scenario);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await controller.RunCurrentAsync(cancellation.Token);

        result.Status.Should().Be(CueStatus.Cancelled);
        controller.TryAdvanceToNext().Should().BeFalse();
        controller.State.Cues[1].Status.Should().Be(CueStatus.Locked);
    }

    [Fact]
    public async Task RunCurrentAsync_DuplicateActivationWhileRunning_IsIgnored()
    {
        var harness = new SessionControllerHarness();
        var gate = new TaskCompletionSource();
        harness.Http.Setup(h => h.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns(async () =>
            {
                await gate.Task;
                return HttpActionResult.Ok(200, "{}");
            });
        var scenario = TestScenarios.Build(
            TestScenarios.SimpleCue("a", actions: [new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.PaymentsSubmit, Method = "POST" }]));
        var controller = harness.Build(scenario);

        var firstRun = controller.RunCurrentAsync(CancellationToken.None);
        // The cue is now Running; a second, concurrent activation must be a no-op, not a second request.
        var duplicate = await controller.RunCurrentAsync(CancellationToken.None);

        duplicate.Status.Should().Be(CueStatus.Running);
        gate.SetResult();
        var result = await firstRun;
        result.Status.Should().Be(CueStatus.Passed);

        harness.Http.Verify(
            h => h.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()),
            Times.Once);
    }

    [Fact]
    public async Task TryAdvanceToNext_AfterPassed_UnlocksNextCue()
    {
        var harness = new SessionControllerHarness();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a"), TestScenarios.SimpleCue("b"));
        var controller = harness.Build(scenario);

        await controller.RunCurrentAsync(CancellationToken.None);
        var advanced = controller.TryAdvanceToNext();

        advanced.Should().BeTrue();
        controller.State.CurrentCueIndex.Should().Be(1);
        controller.State.Cues[1].Status.Should().Be(CueStatus.Available);
    }

    [Fact]
    public async Task SendHttp_TwoActionsSharingIdempotencyKeyRef_ResolveToTheSameKey()
    {
        var harness = new SessionControllerHarness();
        string? firstKey = null;
        string? secondKey = null;
        var callIndex = 0;
        harness.Http.Setup(h => h.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns((string _, string _, string? _, string? key, CancellationToken _, IReadOnlyDictionary<string, string>? _, string? _) =>
            {
                if (callIndex++ == 0)
                {
                    firstKey = key;
                }
                else
                {
                    secondKey = key;
                }

                return Task.FromResult(HttpActionResult.Ok(200, "{}"));
            });

        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("s42", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.CoreBankTransactionsProcess, Method = "POST", IdempotencyKeyRef = "inbox-demo" },
            new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.CoreBankTransactionsProcess, Method = "POST", IdempotencyKeyRef = "inbox-demo" },
        ]));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Passed);
        firstKey.Should().NotBeNullOrEmpty();
        firstKey.Should().Be(secondKey);
    }

    [Fact]
    public async Task AssertHttp_CaptureComparison_EqualCaptures_Passes()
    {
        var harness = new SessionControllerHarness();
        harness.Http.Setup(h => h.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(HttpActionResult.Ok(200, """{"transactionId":"same-id"}"""));

        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("s42", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.CoreBankTransactionsProcess, Method = "POST", IdempotencyKeyRef = "k", CaptureAs = "A", CaptureJsonPath = "$.transactionId" },
            new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.CoreBankTransactionsProcess, Method = "POST", IdempotencyKeyRef = "k", CaptureAs = "B", CaptureJsonPath = "$.transactionId" },
            new ScenarioActionDefinition { Kind = ActionKind.AssertHttp, CaptureRefA = "A", CaptureRefB = "B", ExpectEqual = true },
        ]));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Passed);
    }

    [Fact]
    public async Task AssertHttp_CaptureComparison_DifferentCaptures_Fails()
    {
        var harness = new SessionControllerHarness();
        var callIndex = 0;
        harness.Http.Setup(h => h.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns(() => Task.FromResult(HttpActionResult.Ok(200, "{\"transactionId\":\"id-" + callIndex++ + "\"}")));

        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("s42", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.CoreBankTransactionsProcess, Method = "POST", CaptureAs = "A", CaptureJsonPath = "$.transactionId" },
            new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.CoreBankTransactionsProcess, Method = "POST", CaptureAs = "B", CaptureJsonPath = "$.transactionId" },
            new ScenarioActionDefinition { Kind = ActionKind.AssertHttp, CaptureRefA = "A", CaptureRefB = "B", ExpectEqual = true },
        ]));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Failed);
    }

    [Fact]
    public async Task RetryCurrentAsync_WhenFailed_ReRunsFullActionListWithSameIdempotencyKey()
    {
        var harness = new SessionControllerHarness();
        var keys = new List<string?>();
        var attempt = 0;
        harness.Http.Setup(h => h.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns((string _, string _, string? _, string? key, CancellationToken _, IReadOnlyDictionary<string, string>? _, string? _) =>
            {
                keys.Add(key);
                attempt++;
                return Task.FromResult(attempt == 1 ? HttpActionResult.Error(500, "first try fails") : HttpActionResult.Ok(200, "{}"));
            });

        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.PaymentsSubmit, Method = "POST", IdempotencyKeyRef = "k" },
        ]));
        var controller = harness.Build(scenario);

        var firstAttempt = await controller.RunCurrentAsync(CancellationToken.None);
        firstAttempt.Status.Should().Be(CueStatus.Failed);

        var retryResult = await controller.RetryCurrentAsync(CancellationToken.None);

        retryResult.Status.Should().Be(CueStatus.Passed);
        retryResult.RetryCount.Should().Be(1);
        keys.Should().HaveCount(2);
        keys[0].Should().Be(keys[1]);
    }

    [Fact]
    public async Task RetryCurrentAsync_WhenAmbiguous_OnlyReRunsNonMutatingActions()
    {
        var harness = new SessionControllerHarness();
        var paymentsSubmitCalls = 0;
        harness.Http.Setup(h => h.SendAsync(KnownEndpoints.PaymentsSubmit, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns(() =>
            {
                paymentsSubmitCalls++;
                return Task.FromResult(HttpActionResult.Timeout("ambiguous"));
            });
        harness.Http.Setup(h => h.SendAsync(KnownEndpoints.PaymentsInbox, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(HttpActionResult.Ok(200, """{"ok":"true"}"""));

        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.PaymentsSubmit, Method = "POST", IdempotencyKeyRef = "k" },
            new ScenarioActionDefinition { Kind = ActionKind.AssertHttp, EndpointId = KnownEndpoints.PaymentsInbox, CaptureJsonPath = "$.ok", ExpectedValue = "true" },
        ]));
        var controller = harness.Build(scenario);

        var firstAttempt = await controller.RunCurrentAsync(CancellationToken.None);
        firstAttempt.Status.Should().Be(CueStatus.Ambiguous);
        paymentsSubmitCalls.Should().Be(1);

        var retryResult = await controller.RetryCurrentAsync(CancellationToken.None);

        retryResult.Status.Should().Be(CueStatus.Passed);
        paymentsSubmitCalls.Should().Be(1, "retrying an Ambiguous cue must reconcile via read/assert only, never resend the mutating action");
    }

    [Fact]
    public async Task PreArmCurrentAsync_Succeeds_MarksCuePreArmedWithoutSendingHttp()
    {
        var harness = new SessionControllerHarness();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a", preArm:
        [
            new ScenarioActionDefinition { Kind = ActionKind.WaitForHealth, ResourceName = KnownResources.CoreBankApi, TimeoutSeconds = 5 },
        ]));
        var controller = harness.Build(scenario);

        var result = await controller.PreArmCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.PreArmed);
        harness.Http.Verify(
            h => h.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()),
            Times.Never);
    }

    [Fact]
    public async Task PreArmCurrentAsync_HealthCheckFails_MarksFailedButNeverFiresCue()
    {
        var harness = new SessionControllerHarness();
        harness.Health.Setup(h => h.WaitForHealthyAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a", preArm:
        [
            new ScenarioActionDefinition { Kind = ActionKind.WaitForHealth, ResourceName = KnownResources.CoreBankApi, TimeoutSeconds = 5 },
        ]));
        var controller = harness.Build(scenario);

        var result = await controller.PreArmCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Failed);
    }

    [Fact]
    public async Task RunAcceptedLoadWorkflow_AllInvariantsPass_MarksCuePassed()
    {
        var harness = new SessionControllerHarness();
        harness.LoadWorkflow.Setup(l => l.RunAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoadWorkflowResult.Success([new InvariantResult("Exactly-once processing", true, "ok")]));
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("load", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.RunAcceptedLoadWorkflow, ProfileName = KnownTopologyProfiles.LoadTest, ExpectedUniqueCount = 100 },
        ]));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Passed);
        controller.LastLoadWorkflowResult!.Invariants.Should().ContainSingle();
    }

    [Fact]
    public async Task RunAcceptedLoadWorkflow_AnyInvariantFails_MarksCueFailed()
    {
        var harness = new SessionControllerHarness();
        harness.LoadWorkflow.Setup(l => l.RunAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoadWorkflowResult.Success([new InvariantResult("Balance conservation", false, "mismatch")]));
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("load", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.RunAcceptedLoadWorkflow, ProfileName = KnownTopologyProfiles.LoadTest },
        ]));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Failed);
    }

    [Fact]
    public async Task OpenKnownUrlFailure_DoesNotFailTheCue()
    {
        var harness = new SessionControllerHarness();
        harness.Browser.Setup(b => b.OpenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("handoff", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.OpenKnownUrl, LinkId = KnownLinks.RepoGitHub },
        ]));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Passed, "broken links fail locally and never gate cue evidence");
    }

    [Fact]
    public async Task ShutdownAsync_StopsOnlyOwnedTopologies()
    {
        var harness = new SessionControllerHarness();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.SelectTopology, ProfileName = KnownTopologyProfiles.Regular },
        ]));
        var controller = harness.Build(scenario);

        harness.Process.Setup(p => p.TryAttachAsync(KnownTopologyProfiles.LoadTest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TopologyHandle(KnownTopologyProfiles.LoadTest, false, null, "attached"));

        await controller.RunCurrentAsync(CancellationToken.None);
        await controller.AttachTopologyAsync(KnownTopologyProfiles.LoadTest, CancellationToken.None);

        await controller.ShutdownAsync(CancellationToken.None);

        harness.Process.Verify(p => p.StopOwnedAsync(It.Is<TopologyHandle>(h => h.ProfileName == KnownTopologyProfiles.Regular), It.IsAny<CancellationToken>()), Times.Once);
        harness.Process.Verify(p => p.StopOwnedAsync(It.Is<TopologyHandle>(h => h.ProfileName == KnownTopologyProfiles.LoadTest), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResumeAsync_PassedCheckpoint_UnlocksNextCueAsAvailable()
    {
        var harness = new SessionControllerHarness();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a"), TestScenarios.SimpleCue("b"));
        harness.Journal.Setup(j => j.TryReadLastCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JournalEntry("run-1", "v-test", "abc123", "1", "a", "assert", CueStatus.Passed, DateTimeOffset.UtcNow, "ok"));
        var controller = harness.Build(scenario);

        await controller.ResumeAsync(CancellationToken.None);

        controller.State.Cues[0].Status.Should().Be(CueStatus.Passed);
        controller.State.Cues[1].Status.Should().Be(CueStatus.Available);
        controller.State.CurrentCueIndex.Should().Be(1);
    }

    [Fact]
    public async Task ResumeAsync_InterruptedRunningCheckpoint_RecoversAsAmbiguousNeverPassed()
    {
        var harness = new SessionControllerHarness();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a"), TestScenarios.SimpleCue("b"));
        harness.Journal.Setup(j => j.TryReadLastCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JournalEntry("run-1", "v-test", "abc123", "2", "b", "run", CueStatus.Running, DateTimeOffset.UtcNow, "in flight"));
        var controller = harness.Build(scenario);

        await controller.ResumeAsync(CancellationToken.None);

        controller.State.Cues[0].Status.Should().Be(CueStatus.Passed);
        controller.State.Cues[1].Status.Should().Be(CueStatus.Ambiguous);
        controller.State.CurrentCueIndex.Should().Be(1);
    }

    [Fact]
    public async Task RunInvestigateAsync_ExecutesConfiguredInvestigateActions()
    {
        var harness = new SessionControllerHarness();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a", investigate:
        [
            new ScenarioActionDefinition { Kind = ActionKind.OpenKnownUrl, LinkId = KnownLinks.Jaeger },
        ]));
        var controller = harness.Build(scenario);

        var outcomes = await controller.RunInvestigateAsync(CancellationToken.None);

        outcomes.Should().ContainSingle();
        harness.Browser.Verify(b => b.OpenAsync(KnownLinks.Jaeger, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DetectAttachableTopologyAsync_DelegatesToProcessAdapter()
    {
        var harness = new SessionControllerHarness();
        var handle = new TopologyHandle(KnownTopologyProfiles.Regular, false, null, "fingerprint");
        harness.Process.Setup(p => p.TryAttachAsync(KnownTopologyProfiles.Regular, It.IsAny<CancellationToken>())).ReturnsAsync(handle);
        var controller = harness.Build(TestScenarios.Build(TestScenarios.SimpleCue("a")));

        var result = await controller.DetectAttachableTopologyAsync(KnownTopologyProfiles.Regular, CancellationToken.None);

        result.Should().Be(handle);
    }

    [Fact]
    public async Task StartTopologyAsync_RecordsHandleInState()
    {
        var harness = new SessionControllerHarness();
        var controller = harness.Build(TestScenarios.Build(TestScenarios.SimpleCue("a")));

        var handle = await controller.StartTopologyAsync(KnownTopologyProfiles.Regular, CancellationToken.None);

        handle.IsOwned.Should().BeTrue();
        controller.State.Topologies[KnownTopologyProfiles.Regular].Should().Be(handle);
    }

    [Fact]
    public async Task ShutdownAsync_WhenCueIsRunning_MarksItCancelledAndJournalsIt()
    {
        var harness = new SessionControllerHarness();
        var controller = harness.Build(TestScenarios.Build(TestScenarios.SimpleCue("a")));
        controller.State.CurrentCue.Status = CueStatus.Running;

        await controller.ShutdownAsync(CancellationToken.None);

        controller.State.CurrentCue.Status.Should().Be(CueStatus.Cancelled);
        harness.Journal.Verify(j => j.AppendAsync(It.Is<JournalEntry>(e => e.State == CueStatus.Cancelled), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResumeAsync_NoCheckpoint_LeavesSessionAtInitialState()
    {
        var harness = new SessionControllerHarness();
        harness.Journal.Setup(j => j.TryReadLastCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((JournalEntry?)null);
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a"), TestScenarios.SimpleCue("b"));
        var controller = harness.Build(scenario);

        await controller.ResumeAsync(CancellationToken.None);

        controller.State.CurrentCueIndex.Should().Be(0);
        controller.State.Cues[0].Status.Should().Be(CueStatus.Available);
    }

    [Fact]
    public async Task ResumeAsync_CheckpointForUnknownCue_IsIgnored()
    {
        var harness = new SessionControllerHarness();
        harness.Journal.Setup(j => j.TryReadLastCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JournalEntry("run-1", "v-test", "abc123", "9", "no-such-cue", "assert", CueStatus.Passed, DateTimeOffset.UtcNow, "ok"));
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a"));
        var controller = harness.Build(scenario);

        await controller.ResumeAsync(CancellationToken.None);

        controller.State.CurrentCueIndex.Should().Be(0);
        controller.State.Cues[0].Status.Should().Be(CueStatus.Available);
    }

    [Fact]
    public async Task ResumeAsync_PassedCheckpointOnLastCue_StaysOnLastCue()
    {
        var harness = new SessionControllerHarness();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a"));
        harness.Journal.Setup(j => j.TryReadLastCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JournalEntry("run-1", "v-test", "abc123", "1", "a", "assert", CueStatus.Passed, DateTimeOffset.UtcNow, "ok"));
        var controller = harness.Build(scenario);

        await controller.ResumeAsync(CancellationToken.None);

        controller.State.CurrentCueIndex.Should().Be(0);
        controller.State.Cues[0].Status.Should().Be(CueStatus.Passed);
    }

    [Fact]
    public async Task PreArmCurrentAsync_WhenAlreadyBusy_IsIgnored()
    {
        var harness = new SessionControllerHarness();
        var controller = harness.Build(TestScenarios.Build(TestScenarios.SimpleCue("a", preArm: [new ScenarioActionDefinition { Kind = ActionKind.WaitForHealth, ResourceName = KnownResources.CoreBankApi, TimeoutSeconds = 5 }])));
        controller.State.IsBusy = true;

        var result = await controller.PreArmCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Available);
    }

    [Fact]
    public async Task SelectTopology_CalledTwiceForSameProfileWithinACue_SecondCallIsANoOp()
    {
        var harness = new SessionControllerHarness();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.SelectTopology, ProfileName = KnownTopologyProfiles.Regular },
            new ScenarioActionDefinition { Kind = ActionKind.SelectTopology, ProfileName = KnownTopologyProfiles.Regular },
        ]));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Passed);
        harness.Process.Verify(p => p.StartOwnedAsync(KnownTopologyProfiles.Regular, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SelectTopology_SwitchingToADifferentOwnedProfile_StopsThePreviousOwnedTopologyFirst()
    {
        var harness = new SessionControllerHarness();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.SelectTopology, ProfileName = KnownTopologyProfiles.Regular },
            new ScenarioActionDefinition { Kind = ActionKind.SelectTopology, ProfileName = KnownTopologyProfiles.LoadTest },
        ]));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Passed);
        harness.Process.Verify(p => p.StopOwnedAsync(It.Is<TopologyHandle>(h => h.ProfileName == KnownTopologyProfiles.Regular), It.IsAny<CancellationToken>()), Times.Once,
            "the Regular and LoadTest AppHosts bind the same corebank-api port, so switching profiles must free it first");
        controller.State.Topologies.Should().NotContainKey(KnownTopologyProfiles.Regular);
        controller.State.Topologies.Should().ContainKey(KnownTopologyProfiles.LoadTest);
    }

    [Fact]
    public async Task SelectTopology_SwitchingAwayFromAnAttachedProfile_NeverStopsIt()
    {
        var harness = new SessionControllerHarness();
        harness.Process.Setup(p => p.TryAttachAsync(KnownTopologyProfiles.Regular, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TopologyHandle(KnownTopologyProfiles.Regular, false, null, "attached"));
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.SelectTopology, ProfileName = KnownTopologyProfiles.Regular },
            new ScenarioActionDefinition { Kind = ActionKind.SelectTopology, ProfileName = KnownTopologyProfiles.LoadTest },
        ]));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Passed);
        harness.Process.Verify(p => p.StopOwnedAsync(It.IsAny<TopologyHandle>(), It.IsAny<CancellationToken>()), Times.Never,
            "an attached (unowned) topology is never stopped, even when the session moves on to a different profile");
        controller.State.Topologies.Should().ContainKey(KnownTopologyProfiles.Regular);
    }

    [Fact]
    public async Task SelectTopology_ExistingHealthyTopologyIsAttachable_AttachesInsteadOfStarting()
    {
        var harness = new SessionControllerHarness();
        harness.Process.Setup(p => p.TryAttachAsync(KnownTopologyProfiles.Regular, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TopologyHandle(KnownTopologyProfiles.Regular, false, null, "attached"));
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.SelectTopology, ProfileName = KnownTopologyProfiles.Regular },
        ]));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Passed);
        controller.State.Topologies[KnownTopologyProfiles.Regular].IsOwned.Should().BeFalse();
        harness.Process.Verify(p => p.StartOwnedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AssertHttp_EndpointModeTimesOut_MarksCueAmbiguous()
    {
        var harness = new SessionControllerHarness();
        harness.Http.Setup(h => h.SendAsync(KnownEndpoints.PaymentsInbox, "GET", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(HttpActionResult.Timeout("no response"));
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.AssertHttp, EndpointId = KnownEndpoints.PaymentsInbox, CaptureJsonPath = "$.ok", ExpectedValue = "true" },
        ]));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Ambiguous);
    }

    [Fact]
    public async Task AssertHttp_EndpointModeCallFails_MarksCueFailed()
    {
        var harness = new SessionControllerHarness();
        harness.Http.Setup(h => h.SendAsync(KnownEndpoints.PaymentsInbox, "GET", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(HttpActionResult.Error(500, "boom"));
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.AssertHttp, EndpointId = KnownEndpoints.PaymentsInbox, CaptureJsonPath = "$.ok", ExpectedValue = "true" },
        ]));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Failed);
    }

    [Fact]
    public async Task RunInvestigateAsync_SendHttpWithPathParamRef_ResolvesPathParameterFromEarlierCapture()
    {
        var harness = new SessionControllerHarness();
        string? capturedPathParameter = null;
        harness.Http
            .Setup(h => h.SendAsync(KnownEndpoints.CoreBankTransactionsProcess, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<string?>()))
            .ReturnsAsync(HttpActionResult.Ok(202, """{"transactionId":"deterministic-key"}"""));
        harness.Http
            .Setup(h => h.SendAsync(KnownEndpoints.CoreBankTransactionsStatus, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<string?>()))
            .Callback((string _, string _, string? _, string? _, CancellationToken _, IReadOnlyDictionary<string, string>? _, string? pathParameter) => capturedPathParameter = pathParameter)
            .ReturnsAsync(HttpActionResult.Ok(200, """{"status":"Completed"}"""));

        var scenario = TestScenarios.Build(TestScenarios.SimpleCue(
            "s42",
            actions: [new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.CoreBankTransactionsProcess, Method = "POST", CaptureAs = "FirstTransactionId", CaptureJsonPath = "$.transactionId" }],
            investigate: [new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.CoreBankTransactionsStatus, Method = "GET", PathParamRef = "FirstTransactionId" }]));
        var controller = harness.Build(scenario);
        await controller.RunCurrentAsync(CancellationToken.None);

        var outcomes = await controller.RunInvestigateAsync(CancellationToken.None);

        outcomes.Should().ContainSingle(o => o.Success);
        capturedPathParameter.Should().Be("deterministic-key");
    }

    [Fact]
    public async Task ExecuteActionAsync_UnrecognizedActionKind_MarksCueFailed()
    {
        var harness = new SessionControllerHarness();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a", actions: [new ScenarioActionDefinition { Kind = (ActionKind)999 }]));
        var controller = harness.Build(scenario);

        var result = await controller.RunCurrentAsync(CancellationToken.None);

        result.Status.Should().Be(CueStatus.Failed);
        result.EvidenceSummary.Should().Contain("Unrecognized action kind");
    }
}
