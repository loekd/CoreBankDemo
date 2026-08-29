using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Application.Scenarios;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Moq;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests;

public class RehearsalRunnerTests
{
    [Fact]
    public async Task RunAsync_AllCuesPass_SavesProofPackAndReturnsZero()
    {
        var harness = new SessionControllerHarness();
        var proofPacks = new Mock<IProofPackStore>();
        var scenario = TestScenarios.Build(TestScenarios.SimpleCue("a"), TestScenarios.SimpleCue("b"));
        var controller = harness.Build(scenario, sourceCommit: "deadbeef");

        var exitCode = await RehearsalRunner.RunAsync(controller, proofPacks.Object, CancellationToken.None);

        exitCode.Should().Be(0);
        proofPacks.Verify(
            p => p.SaveAsLatestKnownGoodAsync(
                It.Is<ProofPack>(pack =>
                    pack.ScenarioName == controller.State.ScenarioName &&
                    pack.SourceCommit == "deadbeef" &&
                    pack.CueResults.Count == 2 &&
                    pack.CueResults.All(c => c.Passed)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_ACueFails_ReturnsNonZeroAndNeverSavesProofPack()
    {
        var harness = new SessionControllerHarness();
        harness.Http.Setup(h => h.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(HttpActionResult.Error(500, "boom"));
        var proofPacks = new Mock<IProofPackStore>();
        var scenario = TestScenarios.Build(
            TestScenarios.SimpleCue("a", actions: [new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.PaymentsSubmit, Method = "POST" }]),
            TestScenarios.SimpleCue("b"));
        var controller = harness.Build(scenario);

        var exitCode = await RehearsalRunner.RunAsync(controller, proofPacks.Object, CancellationToken.None);

        exitCode.Should().Be(1);
        proofPacks.Verify(p => p.SaveAsLatestKnownGoodAsync(It.IsAny<ProofPack>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
