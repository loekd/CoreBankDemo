using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Application.Scenarios;
using CoreBankDemo.DemoRunner.Application.StateMachine;
using CoreBankDemo.DemoRunner.Terminal;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Moq;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

public class PresentationModelBuilderTests
{
    [Fact]
    public async Task Build_AvailableCue_ShowsRunEnabledAndNextDisabled()
    {
        var harness = new SessionControllerHarness();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a"), TestScenarios.SimpleCue("b"));
        var controller = harness.Build(scenario);

        var model = PresentationModelBuilder.Build(controller, new Dictionary<string, HealthStatus> { ["payments-api"] = HealthStatus.Healthy });

        model.Current.CanRun.Should().BeTrue();
        model.Current.CanNext.Should().BeFalse();
        model.Current.CanRetry.Should().BeFalse();
        model.Cues.Should().HaveCount(2);
        model.Cues[0].StatusSymbol.Should().Be("○");
        model.Cues[1].StatusSymbol.Should().Be("○");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Build_PassedCue_ShowsNextEnabledAndCorrectSymbol()
    {
        var harness = new SessionControllerHarness();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a"), TestScenarios.SimpleCue("b"));
        var controller = harness.Build(scenario);
        await controller.RunCurrentAsync(CancellationToken.None);

        var model = PresentationModelBuilder.Build(controller, new Dictionary<string, HealthStatus>());

        model.Current.CanNext.Should().BeTrue();
        model.Cues[0].StatusSymbol.Should().Be("✓");
    }

    [Fact]
    public async Task Build_FailedCue_ShowsRetryEnabledAndFailSymbol()
    {
        var harness = new SessionControllerHarness();
        harness.Http.Setup(h => h.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(HttpActionResult.Error(500, "boom"));
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a", actions:
        [
            new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.PaymentsSubmit, Method = "POST" },
        ]));
        var controller = harness.Build(scenario);
        await controller.RunCurrentAsync(CancellationToken.None);

        var model = PresentationModelBuilder.Build(controller, new Dictionary<string, HealthStatus>());

        model.Current.CanRetry.Should().BeTrue();
        model.Current.CanNext.Should().BeFalse();
        model.Cues[0].StatusSymbol.Should().Be("✗");
    }

    [Fact]
    public void Build_ConfidenceRows_MapHealthStatusToSymbol()
    {
        var harness = new SessionControllerHarness();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a"));
        var controller = harness.Build(scenario);

        var model = PresentationModelBuilder.Build(controller, new Dictionary<string, HealthStatus>
        {
            ["payments-api"] = HealthStatus.Healthy,
            ["corebank-api"] = HealthStatus.Unhealthy,
            ["postgres"] = HealthStatus.Unknown,
        });

        model.Confidence.Single(c => c.ResourceName == "payments-api").Symbol.Should().Be("●");
        model.Confidence.Single(c => c.ResourceName == "corebank-api").Symbol.Should().Be("✗");
        model.Confidence.Single(c => c.ResourceName == "postgres").Symbol.Should().Be("◐");
    }
}
