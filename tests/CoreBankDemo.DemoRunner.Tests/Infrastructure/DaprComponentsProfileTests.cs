using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Infrastructure;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

/// <summary>
/// Ties <see cref="ProfileRegistry.DaprComponentsDirectory"/> to the components the console's
/// sidecar is actually started with. A wrong directory here is the quietest failure the feed
/// has: the sidecar starts, reports healthy, joins a broker nobody is publishing to, and the
/// console shows an empty feed with no error to explain it. So these assertions read the real
/// checked-in manifests rather than restating the same literals in the same assembly.
/// </summary>
public class DaprComponentsProfileTests
{
    [Theory]
    [InlineData(TopologyProfile.Regular, "6379")]
    [InlineData(TopologyProfile.LoadTests, "6381")]
    public void ComponentsDirectory_PointsAtTheBrokerThatProfileActuallyPublishesTo(
        TopologyProfile profile,
        string expectedRedisPort)
    {
        var root = CheckedInDevProxyProfileTests.RepositoryRoot();

        var directory = ProfileRegistry.DaprComponentsDirectory(root, profile);

        Directory.Exists(directory).Should().BeTrue($"{directory} is the checked-in components directory for {profile}");
        var pubsub = Path.Combine(directory, "pubsub-redis.yaml");
        File.Exists(pubsub).Should().BeTrue("the sidecar needs the pubsub component to subscribe at all");
        File.ReadAllText(pubsub).Should().Contain(
            $"localhost:{expectedRedisPort}",
            $"{profile}'s Redis listens on {expectedRedisPort}, and a sidecar pointed elsewhere joins a broker nobody is publishing to");
    }

    [Fact]
    public void ComponentsDirectories_AreNotInterchangeable()
    {
        var root = CheckedInDevProxyProfileTests.RepositoryRoot();

        ProfileRegistry.DaprComponentsDirectory(root, TopologyProfile.Regular)
            .Should().NotBe(ProfileRegistry.DaprComponentsDirectory(root, TopologyProfile.LoadTests));
    }

    [Fact]
    public void ComponentsDirectory_RefusesAProfileWithNoTopology()
    {
        var root = CheckedInDevProxyProfileTests.RepositoryRoot();

        var act = () => ProfileRegistry.DaprComponentsDirectory(root, TopologyProfile.None);

        act.Should().Throw<ArgumentOutOfRangeException>(
            "there is no broker to point a sidecar at when no topology is running");
    }

    /// <summary>
    /// The console declares the three CloudEvent types as local wire records rather than
    /// referencing <c>CoreBankDemo.ServiceDefaults</c>, which is what keeps ADR-015's
    /// project-graph invariant intact — but a copied constant pinned to nothing is a constant
    /// that can drift silently. These read the checked-in subscription manifest, the same
    /// frozen surface PaymentsAPI is routed by, so a typo in either place fails here.
    /// </summary>
    [Theory]
    [InlineData("com.corebank.transaction.completed")]
    [InlineData("com.corebank.transaction.failed")]
    [InlineData("com.corebank.account.balance.updated")]
    public void CopiedEventTypes_MatchTheCheckedInSubscriptionManifest(string eventType)
    {
        foreach (var directory in new[] { "components", "components-loadtest" })
        {
            var manifest = Path.Combine(
                CheckedInDevProxyProfileTests.RepositoryRoot(),
                "dapr",
                directory,
                "subscription-transaction-events.yaml");

            File.ReadAllText(manifest).Should().Contain(
                $"event.type == \"{eventType}\"",
                $"the console's copy of {eventType} must name the same type {directory} routes");
        }
    }

    [Fact]
    public void OutcomeEventTypeConstants_AreExactlyTheThreeTheManifestRoutes()
    {
        OutcomeEventTypes.TransactionCompleted.Should().Be("com.corebank.transaction.completed");
        OutcomeEventTypes.TransactionFailed.Should().Be("com.corebank.transaction.failed");
        OutcomeEventTypes.BalanceUpdated.Should().Be("com.corebank.account.balance.updated");
    }

    [Fact]
    public void SubscribedTopicAndComponent_MatchTheCheckedInManifest()
    {
        var manifest = File.ReadAllText(Path.Combine(
            CheckedInDevProxyProfileTests.RepositoryRoot(),
            "dapr",
            "components",
            "subscription-transaction-events.yaml"));

        manifest.Should().Contain($"topic: {OutcomeEventTypes.Topic}");
        manifest.Should().Contain($"pubsubname: {OutcomeEventTypes.PubSubComponent}");

        // The manifest is scopes: [payments-api] and stays that way. The console's sidecar
        // parses and ignores it, subscribing under its own app-id instead -- which is exactly
        // what makes this a fan-out rather than a diversion.
        manifest.Should().Contain("scopes:").And.Contain("payments-api");
        DaprOutcomeFeed.AppId.Should().NotBe("payments-api");
    }
}
