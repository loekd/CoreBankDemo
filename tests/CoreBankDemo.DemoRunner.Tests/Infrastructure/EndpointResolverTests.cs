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

    /// <summary>
    /// The console reaches CoreBank's cancellation endpoint only through this allow-listed id:
    /// ADR-015 forbids operator-supplied URLs, and Cancel adds no endpoint to any banking service.
    /// </summary>
    [Fact]
    public void EndpointFor_TransactionCancel_IsAPostToCoreBanksOwnEndpointOnBothProfiles()
    {
        foreach (var profile in new[] { TopologyProfile.Regular, TopologyProfile.LoadTests })
        {
            var (url, method) = EndpointResolver.EndpointFor(profile, KnownEndpoints.TransactionCancel);
            url.Should().Be("http://127.0.0.1:5032/api/transactions/cancel");
            method.Should().Be(HttpMethod.Post);
        }
    }

    [Fact]
    public void LinkFor_AllowsOnlyAspireAndLgtm()
    {
        EndpointResolver.LinkFor(KnownLinks.Lgtm).Should().Be("http://localhost:3000/d/corebank");
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
    [InlineData(KnownEndpoints.TransactionCancel, null, "5032/api/transactions/cancel")]
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
    [InlineData(KnownResources.Lgtm, TopologyProfile.Regular, "3000/api/health")]
    [InlineData(KnownResources.Postgres, TopologyProfile.Regular, "5032")]
    [InlineData(KnownResources.Redis, TopologyProfile.Regular, "5032")]
    public void HealthUrlFor_AllKnownHttpProbesResolve(
        string resource,
        TopologyProfile profile,
        string expectedPort)
    {
        EndpointResolver.HealthUrlFor(resource, profile).Should().Contain(expectedPort);
    }

    /// <summary>
    /// The LGTM container is persistent under both AppHosts, so Doctor's port checks must
    /// know about port 3000 in the LoadTests profile as well, not only the Regular one.
    /// </summary>
    [Fact]
    public void ProfilePorts_ListLgtmOnPort3000ForBothProfiles()
    {
        EndpointResolver.RegularProfilePorts.Should().Contain(KnownResources.Lgtm, 3000);
        EndpointResolver.LoadTestProfilePorts.Should().Contain(KnownResources.Lgtm, 3000);
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
