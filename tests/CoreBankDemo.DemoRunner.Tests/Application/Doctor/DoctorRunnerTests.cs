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

        var report = await new DoctorRunner(
                environment.Object,
                health.Object,
                aspire,
                [new DoctorPortRequirement(TopologyProfile.Regular, "payments-api", 5294)])
            .RunAsync(CancellationToken.None);

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

        var report = await new DoctorRunner(
                environment.Object,
                health.Object,
                aspire,
                [new DoctorPortRequirement(TopologyProfile.Regular, "payments-api", 5294)])
            .RunAsync(CancellationToken.None);

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

        var report = await new DoctorRunner(environment.Object, health.Object, aspire, [])
            .RunAsync(CancellationToken.None);

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
            Discovery = TopologyDiscoveryResult.Success(
                [OperatorHarness.Snapshot(TopologyProfile.Regular) with { IsFingerprintMatch = false, ErrorSummary = "partial" }]),
        };

        var report = await new DoctorRunner(environment.Object, health.Object, aspire, [])
            .RunAsync(CancellationToken.None);

        report.AllPassed.Should().BeFalse();
        report.Checks.Should().Contain(check => check.Name.Contains("Regular") && check.Remediation == "partial");
    }

    [Fact]
    public async Task RunAsync_DiscoveryFailure_IsStoredAsUnreachableAndBlocksStart()
    {
        var environment = ReadyEnvironment();
        var health = new Mock<IHealthMonitor>();
        var aspire = new FakeAspireAdapter
        {
            Discovery = TopologyDiscoveryResult.Unreachable("aspire ps timed out"),
        };

        var report = await new DoctorRunner(environment.Object, health.Object, aspire, [])
            .RunAsync(CancellationToken.None);

        report.DiscoveryReachable.Should().BeFalse();
        report.CanStart(TopologyProfile.Regular).Should().BeFalse();
        report.Checks.Should().Contain(check => check.Name == "Aspire discovery" && !check.Passed);
    }

    [Fact]
    public async Task RunAsync_UnrelatedProfilePortFailure_DoesNotBlockReadyTargetProfile()
    {
        var environment = ReadyEnvironment();
        environment.Setup(probe => probe.IsPortFreeAsync(5294, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        environment.Setup(probe => probe.IsPortFreeAsync(5295, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var health = new Mock<IHealthMonitor>();
        health.Setup(probe => probe.CheckAsync("payments-api", TopologyProfile.LoadTests, It.IsAny<CancellationToken>()))
            .ReturnsAsync(HealthStatus.Unreachable);
        var aspire = new FakeAspireAdapter();
        var requirements = new[]
        {
            new DoctorPortRequirement(TopologyProfile.Regular, "payments-api", 5294),
            new DoctorPortRequirement(TopologyProfile.LoadTests, "payments-api", 5295),
        };

        var report = await new DoctorRunner(environment.Object, health.Object, aspire, requirements)
            .RunAsync(CancellationToken.None);

        report.CanStart(TopologyProfile.Regular).Should().BeTrue();
        report.CanStart(TopologyProfile.LoadTests).Should().BeFalse();
    }

    private static Mock<IEnvironmentProbe> ReadyEnvironment()
    {
        var environment = new Mock<IEnvironmentProbe>();
        environment.Setup(probe => probe.IsDotnetSdkAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        environment.Setup(probe => probe.IsAspireCliAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        environment.Setup(probe => probe.IsContainerRuntimeAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        environment.Setup(probe => probe.IsPortFreeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return environment;
    }
}
