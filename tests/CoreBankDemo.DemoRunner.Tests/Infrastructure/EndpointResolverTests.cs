using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Infrastructure;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

public class EndpointResolverTests
{
    [Fact]
    public void EndpointFor_UsesProfileSpecificPaymentsPort()
    {
        EndpointResolver.EndpointFor(TopologyProfile.Regular, KnownEndpoints.PaymentsSubmit).Url
            .Should().Contain(":5294/");
        EndpointResolver.EndpointFor(TopologyProfile.LoadTests, KnownEndpoints.PaymentsSubmit).Url
            .Should().Contain(":5295/");
    }

    [Fact]
    public void EndpointFor_RejectsUnknownOrRegularResetTargets()
    {
        var unknown = () => EndpointResolver.EndpointFor(TopologyProfile.Regular, "arbitrary");
        var regularReset = () => EndpointResolver.EndpointFor(TopologyProfile.Regular, KnownEndpoints.LoadReset);
        var missingPath = () => EndpointResolver.EndpointFor(TopologyProfile.Regular, KnownEndpoints.TransactionOutcome);

        unknown.Should().Throw<ArgumentOutOfRangeException>();
        regularReset.Should().Throw<ArgumentOutOfRangeException>();
        missingPath.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void LinkFor_AllowsOnlyAspireAndJaeger()
    {
        EndpointResolver.LinkFor(KnownLinks.Jaeger).Should().Contain("16686");
        Action aspireRequiresLiveState = () => EndpointResolver.LinkFor(KnownLinks.AspireDashboard);
        aspireRequiresLiveState.Should().Throw<ArgumentOutOfRangeException>();
        Action arbitrary = () => EndpointResolver.LinkFor("https://example.com");
        arbitrary.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(KnownEndpoints.TransactionOutcome, "key", "5032")]
    [InlineData(KnownEndpoints.LoadReset, null, "5181/reset")]
    [InlineData(KnownEndpoints.LoadDrain, null, "5181/assert/drain")]
    [InlineData(KnownEndpoints.LoadAssert, null, "5181/assert/results")]
    [InlineData(KnownEndpoints.PaymentsOutbox, null, "5181/payments/outbox")]
    [InlineData(KnownEndpoints.PaymentsInbox, null, "5181/payments/inbox")]
    [InlineData(KnownEndpoints.CoreBankInbox, null, "5181/corebank/inbox")]
    [InlineData(KnownEndpoints.CoreBankOutbox, null, "5181/corebank/outbox")]
    public void EndpointFor_AllCompiledEndpointsResolve(
        string endpoint,
        string? path,
        string expected)
    {
        EndpointResolver.EndpointFor(TopologyProfile.LoadTests, endpoint, path).Url.Should().Contain(expected);
    }

    [Theory]
    [InlineData(KnownResources.PaymentsApi, TopologyProfile.Regular, "5294")]
    [InlineData(KnownResources.PaymentsApi, TopologyProfile.LoadTests, "5295")]
    [InlineData(KnownResources.CoreBankApi, TopologyProfile.Regular, "5032")]
    [InlineData(KnownResources.LoadTestSupport, TopologyProfile.LoadTests, "5181")]
    [InlineData(KnownResources.Jaeger, TopologyProfile.Regular, "16686")]
    [InlineData(KnownResources.Postgres, TopologyProfile.Regular, "5032")]
    [InlineData(KnownResources.Redis, TopologyProfile.Regular, "5032")]
    public void HealthUrlFor_AllKnownHttpProbesResolve(
        string resource,
        TopologyProfile profile,
        string expectedPort)
    {
        EndpointResolver.HealthUrlFor(resource, profile).Should().Contain(expectedPort);
    }

    [Fact]
    public void ProfileRegistry_ResolvesBothExactKnownProjects()
    {
        ProfileRegistry.RelativeProjectPath(TopologyProfile.Regular).Should().Be("CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj");
        ProfileRegistry.RelativeProjectPath(TopologyProfile.LoadTests).Should().Be("CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj");
        ProfileRegistry.ProjectPath("/repo", TopologyProfile.Regular).Should().Be("/repo/CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj");
        Action invalid = () => ProfileRegistry.RelativeProjectPath(TopologyProfile.None);
        invalid.Should().Throw<ArgumentOutOfRangeException>();
    }
}
