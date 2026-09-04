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

    [Fact]
    public async Task RunAsync_RepeatedPolls_DoNotReshellTheCliProbesEveryTime()
    {
        var environment = ReadyEnvironment();
        var time = new FakeTimeProvider();
        var runner = new DoctorRunner(environment.Object, new Mock<IHealthMonitor>().Object, new FakeAspireAdapter(), [], time);

        await runner.RunAsync(CancellationToken.None);
        await runner.RunAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(16));
        await runner.RunAsync(CancellationToken.None);

        environment.Verify(probe => probe.IsContainerRuntimeAvailableAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RunAsync_OccupiedPort_NamesThePortAndHowToFindItsHolder()
    {
        var environment = ReadyEnvironment();
        environment.Setup(probe => probe.IsPortFreeAsync(5032, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var health = new Mock<IHealthMonitor>();
        health.Setup(probe => probe.CheckAsync("corebank-api", TopologyProfile.Regular, It.IsAny<CancellationToken>()))
            .ReturnsAsync(HealthStatus.Unreachable);
        var aspire = new FakeAspireAdapter();

        var report = await new DoctorRunner(
                environment.Object,
                health.Object,
                aspire,
                [new DoctorPortRequirement(TopologyProfile.Regular, "corebank-api", 5032)])
            .RunAsync(CancellationToken.None);

        var portCheck = report.Checks.Single(check => check.Name.StartsWith("Port ", StringComparison.Ordinal));
        portCheck.Passed.Should().BeFalse();
        portCheck.Remediation.Should().Contain("5032").And.Contain("lsof").And.Contain("docker ps");
        report.Profiles[TopologyProfile.Regular].Detail.Should().Contain("5032");
    }

    [Fact]
    public async Task RunAsync_HealthyPersistentContainerHoldingItsPort_LeavesStartAvailable()
    {
        // Jaeger, Postgres and Redis are declared ContainerLifetime.Persistent, so they keep
        // their published ports after an AppHost stops and Aspire reuses them on the next start.
        var environment = ReadyEnvironment();
        environment.Setup(probe => probe.IsPortFreeAsync(16686, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var health = new Mock<IHealthMonitor>();
        health.Setup(probe => probe.CheckAsync(KnownResources.Jaeger, TopologyProfile.Regular, It.IsAny<CancellationToken>()))
            .ReturnsAsync(HealthStatus.Healthy);

        var report = await new DoctorRunner(
                environment.Object,
                health.Object,
                new FakeAspireAdapter(),
                [new DoctorPortRequirement(TopologyProfile.Regular, KnownResources.Jaeger, 16686)])
            .RunAsync(CancellationToken.None);

        report.CanStart(TopologyProfile.Regular).Should().BeTrue();
        report.Checks.Single(check => check.Name.StartsWith("Port ", StringComparison.Ordinal))
            .Remediation.Should().Contain("persistent").And.Contain("reuses");
        report.Profiles[TopologyProfile.Regular].Detail.Should().Be("not running — Start available");
    }

    [Fact]
    public async Task RunAsync_SilentPersistentContainer_PointsAtTheContainerNotAProcess()
    {
        var environment = ReadyEnvironment();
        environment.Setup(probe => probe.IsPortFreeAsync(16686, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var health = new Mock<IHealthMonitor>();
        health.Setup(probe => probe.CheckAsync(KnownResources.Jaeger, TopologyProfile.Regular, It.IsAny<CancellationToken>()))
            .ReturnsAsync(HealthStatus.Unreachable);

        var report = await new DoctorRunner(
                environment.Object,
                health.Object,
                new FakeAspireAdapter(),
                [new DoctorPortRequirement(TopologyProfile.Regular, KnownResources.Jaeger, 16686)])
            .RunAsync(CancellationToken.None);

        report.CanStart(TopologyProfile.Regular).Should().BeFalse();
        report.Checks.Single(check => check.Name.StartsWith("Port ", StringComparison.Ordinal))
            .Remediation.Should().Contain("persistent jaeger container").And.Contain("docker ps --filter publish=16686");
    }

    [Fact]
    public async Task RunAsync_HealthyEndpointsButNoAspireProcess_SaysItWasStartedOutsideTheCli()
    {
        var environment = ReadyEnvironment();
        environment.Setup(probe => probe.IsPortFreeAsync(5294, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        environment.Setup(probe => probe.IsPortFreeAsync(5032, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var health = new Mock<IHealthMonitor>();
        health.Setup(probe => probe.CheckAsync(It.IsAny<string>(), TopologyProfile.Regular, It.IsAny<CancellationToken>()))
            .ReturnsAsync(HealthStatus.Healthy);
        var requirements = new[]
        {
            new DoctorPortRequirement(TopologyProfile.Regular, KnownResources.PaymentsApi, 5294),
            new DoctorPortRequirement(TopologyProfile.Regular, KnownResources.CoreBankApi, 5032),
        };

        var report = await new DoctorRunner(environment.Object, health.Object, new FakeAspireAdapter(), requirements)
            .RunAsync(CancellationToken.None);

        report.CanStart(TopologyProfile.Regular).Should().BeFalse();
        report.Profiles[TopologyProfile.Regular].CanAttach.Should().BeFalse();
        report.Profiles[TopologyProfile.Regular].Detail.Should()
            .Contain("5294, 5032")
            .And.Contain("started outside the Aspire CLI")
            .And.Contain("aspire run");
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
