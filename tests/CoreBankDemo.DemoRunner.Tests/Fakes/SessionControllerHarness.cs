using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Application.Scenarios;
using CoreBankDemo.DemoRunner.Application.StateMachine;
using Moq;

namespace CoreBankDemo.DemoRunner.Tests.Fakes;

/// <summary>Builds a <see cref="SessionController"/> wired to Moq fakes for every port, with sensible defaults that all succeed.</summary>
public sealed class SessionControllerHarness
{
    public Mock<IProcessAdapter> Process { get; } = new();
    public Mock<IHttpActionExecutor> Http { get; } = new();
    public Mock<IHealthMonitor> Health { get; } = new();
    public Mock<IBrowserLauncher> Browser { get; } = new();
    public Mock<ILoadWorkflowRunner> LoadWorkflow { get; } = new();
    public Mock<IJournal> Journal { get; } = new();
    public FakeTimeProvider Time { get; } = new();

    public SessionControllerHarness()
    {
        Health.Setup(h => h.WaitForHealthyAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        Health.Setup(h => h.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(HealthStatus.Healthy);
        Http.Setup(h => h.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(HttpActionResult.Ok(200, "{}"));
        Browser.Setup(b => b.OpenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        Process.Setup(p => p.StartOwnedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string profile, CancellationToken _) => new TopologyHandle(profile, true, 4242, $"owned:{profile}"));
    }

    public SessionController Build(TalkScenarioDefinition scenario, SessionMode mode = SessionMode.Show, string runId = "run-1", string sourceCommit = "abc123") =>
        new(
            scenario,
            mode,
            runId,
            sourceCommit,
            Process.Object,
            Http.Object,
            Health.Object,
            Browser.Object,
            LoadWorkflow.Object,
            Journal.Object,
            Time);
}

public static class TestScenarios
{
    public static TalkCueDefinition SimpleCue(string id, string slideAnchor = "1", IReadOnlyList<ScenarioActionDefinition>? actions = null, IReadOnlyList<ScenarioActionDefinition>? preArm = null, IReadOnlyList<ScenarioActionDefinition>? investigate = null) =>
        new()
        {
            Id = id,
            SlideAnchor = slideAnchor,
            Title = $"Title {id}",
            SpeakerNote = "Note",
            PreArmActions = preArm ?? [],
            Actions = actions ?? [new ScenarioActionDefinition { Kind = ActionKind.SpeakerPause, Note = "pause" }],
            InvestigateActions = investigate ?? [],
        };

    public static TalkScenarioDefinition Build(params TalkCueDefinition[] cues) => new()
    {
        SchemaVersion = ScenarioValidator.SupportedSchemaVersion,
        Name = "TestScenario",
        ScenarioVersion = "v-test",
        RequiredProfile = KnownTopologyProfiles.Regular,
        Cues = cues,
    };
}
