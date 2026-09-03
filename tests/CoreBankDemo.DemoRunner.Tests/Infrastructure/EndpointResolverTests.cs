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
}
