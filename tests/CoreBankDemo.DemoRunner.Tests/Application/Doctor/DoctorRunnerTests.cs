using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application.Doctor;
using CoreBankDemo.DemoRunner.Application.Ports;
using Moq;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application.Doctor;

public class DoctorRunnerTests
{
    private static (Mock<IEnvironmentProbe> Environment, Mock<IHealthMonitor> Health, DoctorRunner Runner) CreateAllHealthy()
    {
        var environment = new Mock<IEnvironmentProbe>();
        environment.Setup(e => e.IsDotnetSdkAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        environment.Setup(e => e.IsAspireCliAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        environment.Setup(e => e.IsContainerRuntimeAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        environment.Setup(e => e.IsPortFreeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var health = new Mock<IHealthMonitor>();
        health.Setup(h => h.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(HealthStatus.Healthy);

        return (environment, health, new DoctorRunner(environment.Object, health.Object));
    }

    private static string WriteScenario(string json)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "doctor-fixtures");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"doctor-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string ValidScenarioJson = """
        {
          "schemaVersion": 1,
          "name": "Test",
          "scenarioVersion": "v1",
          "requiredProfile": "Regular",
          "cues": [ { "id": "c", "slideAnchor": "1", "title": "T", "speakerNote": "N", "actions": [ { "kind": "speakerPause", "note": "n" } ] } ]
        }
        """;

    [Fact]
    public async Task RunAsync_EverythingHealthy_AllPassed()
    {
        var (_, _, runner) = CreateAllHealthy();
        var path = WriteScenario(ValidScenarioJson);

        var report = await runner.RunAsync(path, new Dictionary<string, int> { ["payments-api"] = 5294 }, CancellationToken.None);

        report.AllPassed.Should().BeTrue();
        report.Checks.Should().Contain(c => c.Name == "Scenario valid" && c.Passed);
    }

    [Fact]
    public async Task RunAsync_InvalidScenario_ReportsFailedCheckAndNeverThrows()
    {
        var (_, _, runner) = CreateAllHealthy();
        var path = WriteScenario("{ not valid json");

        var report = await runner.RunAsync(path, new Dictionary<string, int>(), CancellationToken.None);

        report.AllPassed.Should().BeFalse();
        report.Checks.Single(c => c.Name == "Scenario valid").Passed.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_MissingAspireCli_FailsWithRemediation()
    {
        var (environment, _, runner) = CreateAllHealthy();
        environment.Setup(e => e.IsAspireCliAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var path = WriteScenario(ValidScenarioJson);

        var report = await runner.RunAsync(path, new Dictionary<string, int>(), CancellationToken.None);

        report.AllPassed.Should().BeFalse();
        var check = report.Checks.Single(c => c.Name == "Aspire CLI available");
        check.Passed.Should().BeFalse();
        check.Remediation.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RunAsync_PortOccupiedByHealthyResource_PassesAsAttachable()
    {
        var (environment, health, runner) = CreateAllHealthy();
        environment.Setup(e => e.IsPortFreeAsync(5294, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        health.Setup(h => h.CheckAsync("payments-api", It.IsAny<CancellationToken>())).ReturnsAsync(HealthStatus.Healthy);
        var path = WriteScenario(ValidScenarioJson);

        var report = await runner.RunAsync(path, new Dictionary<string, int> { ["payments-api"] = 5294 }, CancellationToken.None);

        report.AllPassed.Should().BeTrue();
        report.Checks.Single(c => c.Name.Contains("5294")).Remediation.Should().Contain("Attach available");
    }

    [Fact]
    public async Task RunAsync_PortOccupiedByUnhealthyResource_Fails()
    {
        var (environment, health, runner) = CreateAllHealthy();
        environment.Setup(e => e.IsPortFreeAsync(5294, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        health.Setup(h => h.CheckAsync("payments-api", It.IsAny<CancellationToken>())).ReturnsAsync(HealthStatus.Unhealthy);
        var path = WriteScenario(ValidScenarioJson);

        var report = await runner.RunAsync(path, new Dictionary<string, int> { ["payments-api"] = 5294 }, CancellationToken.None);

        report.AllPassed.Should().BeFalse();
    }
}
