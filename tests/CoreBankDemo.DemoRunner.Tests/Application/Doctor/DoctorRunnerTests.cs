using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Doctor;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Moq;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application.Doctor;

public class DoctorRunnerTests
{
    [Fact]
    public async Task RunAsync_AllPrerequisitesAndFreePorts_PassesWithoutHealthCalls()
    {
        var environment = new Mock<IEnvironmentProbe>();
        environment.Setup(probe => probe.IsDotnetSdkAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        environment.Setup(probe => probe.IsAspireCliAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        environment.Setup(probe => probe.IsContainerRuntimeAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        environment.Setup(probe => probe.IsPortFreeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var health = new Mock<IHealthMonitor>();
        var aspire = new FakeAspireAdapter();

        var report = await new DoctorRunner(environment.Object, health.Object, aspire)
            .RunAsync([new DoctorPortRequirement(TopologyProfile.Regular, "payments-api", 5294)], CancellationToken.None);

        report.AllPassed.Should().BeTrue();
        report.Checks.Should().HaveCount(6);
        health.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, true)]
    [InlineData(HealthStatus.Unhealthy, false)]
    [InlineData(HealthStatus.Unknown, false)]
    [InlineData(HealthStatus.Unreachable, false)]
    public async Task RunAsync_OccupiedPort_RequiresHealthyKnownEndpoint(HealthStatus status, bool expected)
    {
        var environment = new Mock<IEnvironmentProbe>();
        environment.Setup(probe => probe.IsDotnetSdkAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        environment.Setup(probe => probe.IsAspireCliAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        environment.Setup(probe => probe.IsContainerRuntimeAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        environment.Setup(probe => probe.IsPortFreeAsync(5294, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var health = new Mock<IHealthMonitor>();
        health.Setup(probe => probe.CheckAsync("payments-api", TopologyProfile.Regular, It.IsAny<CancellationToken>())).ReturnsAsync(status);
        var aspire = new FakeAspireAdapter();

        var report = await new DoctorRunner(environment.Object, health.Object, aspire)
            .RunAsync([new DoctorPortRequirement(TopologyProfile.Regular, "payments-api", 5294)], CancellationToken.None);

        report.Checks.Single(check => check.Name.StartsWith("Port ", StringComparison.Ordinal)).Passed.Should().Be(expected);
    }

    [Fact]
    public async Task RunAsync_MissingPrerequisites_ReportsEveryFailure()
    {
        var environment = new Mock<IEnvironmentProbe>();
        environment.Setup(probe => probe.IsDotnetSdkAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        environment.Setup(probe => probe.IsAspireCliAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        environment.Setup(probe => probe.IsContainerRuntimeAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var health = new Mock<IHealthMonitor>();
        var aspire = new FakeAspireAdapter();

        var report = await new DoctorRunner(environment.Object, health.Object, aspire)
            .RunAsync([], CancellationToken.None);

        report.AllPassed.Should().BeFalse();
        report.Checks.Take(3).Should().OnlyContain(check => !check.Passed);
    }

    [Fact]
    public async Task RunAsync_PartialTopology_FailsPreflightWithReason()
    {
        var environment = new Mock<IEnvironmentProbe>();
        environment.Setup(probe => probe.IsDotnetSdkAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        environment.Setup(probe => probe.IsAspireCliAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        environment.Setup(probe => probe.IsContainerRuntimeAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var health = new Mock<IHealthMonitor>();
        var aspire = new FakeAspireAdapter
        {
            Discovered = [OperatorHarness.Snapshot(TopologyProfile.Regular) with { IsFingerprintMatch = false, ErrorSummary = "partial" }],
        };

        var report = await new DoctorRunner(environment.Object, health.Object, aspire)
            .RunAsync([], CancellationToken.None);

        report.AllPassed.Should().BeFalse();
        report.Checks.Should().Contain(check => check.Name.Contains("Regular") && check.Remediation == "partial");
    }
}
