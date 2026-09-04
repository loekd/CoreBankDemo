using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application;

public class AspireJsonParserTests
{
    [Fact]
    public void Parse_KnownRegularGraph_ProducesFingerprintHealthEndpointsAndReplicaCount()
    {
        const string json =
            """
            {
              "resources": [
                { "name": "postgres-x", "displayName": "postgres", "resourceType":"Container", "state": "Running", "healthStatus": "Healthy" },
                { "name": "redis-x", "displayName": "redis", "resourceType":"Container", "state": "Running", "healthStatus": "Healthy" },
                { "name": "jaeger-x", "displayName": "jaeger", "resourceType":"Container", "state": "Running", "healthStatus": "Healthy", "endpoints": [{"url":"http://localhost:16686"}] },
                { "name": "corebank-api-1", "displayName": "corebank-api", "resourceType":"Project", "state": "Running", "healthStatus": "Healthy", "dashboardUrl":"https://localhost:17253/?resource=corebank-api-1", "urls":[{"url":"http://127.0.0.1:5032/swagger"}], "commands":{"stop":{"state":"Enabled"},"restart":{"state":"Enabled"}} },
                { "name": "corebank-api-2", "displayName": "corebank-api", "resourceType":"Project", "state": "Running", "healthStatus": "Healthy", "urls":[{"url":"http://127.0.0.1:5032/swagger"}], "commands":{"stop":{"state":"Enabled"},"restart":{"state":"Enabled"}} },
                { "name": "corebank-api-dapr-cli-x", "displayName": "corebank-api-dapr-cli", "resourceType":"Executable", "state": "Running", "healthStatus": "Healthy" },
                { "name": "payments-api-1", "displayName": "payments-api", "resourceType":"Project", "state": "Running", "healthStatus": "Healthy", "urls":[{"url":"http://127.0.0.1:5294/swagger"}] },
                { "name": "payments-api-2", "displayName": "payments-api", "resourceType":"Project", "state": "Running", "healthStatus": "Healthy", "urls":[{"url":"http://127.0.0.1:5294/swagger"}] },
                { "name": "devproxy-x", "displayName": "devproxy", "resourceType":"Executable", "state": "Stopped", "healthStatus": "" }
              ]
            }
            """;

        var snapshot = AspireJsonParser.Parse(TopologyProfile.Regular, json, DateTimeOffset.UnixEpoch);

        snapshot.IsFingerprintMatch.Should().BeTrue();
        snapshot.FindResource(KnownResources.CoreBankApi)!.ReplicaCount.Should().Be(2);
        snapshot.FindResource(KnownResources.CoreBankApi)!.InstanceNames.Should().BeEquivalentTo(["corebank-api-1", "corebank-api-2"]);
        snapshot.FindResource(KnownResources.CoreBankApi)!.Supports(ResourceCommand.Stop).Should().BeTrue();
        snapshot.FindResource(KnownResources.CoreBankApi)!.Supports(ResourceCommand.Start).Should().BeFalse();
        snapshot.FindResource(KnownResources.Jaeger)!.Endpoints.Should().Contain("http://localhost:16686");
        snapshot.FindResource(KnownResources.DevProxy)!.Condition.Should().Be(ResourceCondition.Stopped);
        snapshot.DashboardUrl.Should().Be("https://localhost:17253");
    }

    [Theory]
    [InlineData("FailedToStart", "", ResourceCondition.Failed)]
    [InlineData("Waiting", "", ResourceCondition.Starting)]
    [InlineData("Finished", "", ResourceCondition.Completed)]
    [InlineData("Running", "Degraded", ResourceCondition.Degraded)]
    [InlineData("mystery", "", ResourceCondition.Unknown)]
    public void Parse_MapsTruthfulResourceStates(string state, string health, ResourceCondition expected)
    {
        var required = RequiredJson(state, health);

        var snapshot = AspireJsonParser.Parse(TopologyProfile.Regular, required, DateTimeOffset.UnixEpoch);

        snapshot.FindResource(KnownResources.CoreBankApi)!.Condition.Should().Be(expected);
    }

    [Fact]
    public void Parse_MissingRequiredResource_IsFingerprintMismatch()
    {
        var snapshot = AspireJsonParser.Parse(
            TopologyProfile.Regular,
            """{"resources":[{"name":"payments-api","state":"Running","health":"Healthy"}]}""",
            DateTimeOffset.UnixEpoch);

        snapshot.IsReachable.Should().BeTrue();
        snapshot.IsFingerprintMatch.Should().BeFalse();
        snapshot.ErrorSummary.Should().Contain("missing");
    }

    [Fact]
    public void Parse_WrongKnownEndpointPort_IsFingerprintMismatch()
    {
        var json = RequiredJson("Running", "Healthy")
            .Replace("\"corebank-api\", \"resourceState\": \"Running\"", "\"corebank-api\", \"resourceState\": \"Running\", \"urls\":[{\"url\":\"http://127.0.0.1:9999\"}]")
            .Replace("\"payments-api\", \"resourceState\": \"Running\"", "\"payments-api\", \"resourceState\": \"Running\", \"urls\":[{\"url\":\"http://127.0.0.1:5294\"}]")
            .Replace("\"jaeger\", \"resourceState\": \"Running\"", "\"jaeger\", \"resourceState\": \"Running\", \"urls\":[{\"url\":\"http://127.0.0.1:16686\"}]");

        var snapshot = AspireJsonParser.Parse(TopologyProfile.Regular, json, DateTimeOffset.UnixEpoch);

        snapshot.IsFingerprintMatch.Should().BeFalse();
        snapshot.ErrorSummary.Should().Contain("corebank-api expected port 5032");
    }

    [Fact]
    public void Parse_MalformedJson_IsUnknownNotUnreachable()
    {
        var snapshot = AspireJsonParser.Parse(TopologyProfile.Regular, "{", DateTimeOffset.UnixEpoch);

        snapshot.IsReachable.Should().BeTrue();
        snapshot.IsFingerprintMatch.Should().BeFalse();
        snapshot.Resources.Should().OnlyContain(resource => resource.Condition == ResourceCondition.Unknown);
        snapshot.ErrorSummary.Should().Contain("unparseable");
    }

    [Fact]
    public void Snapshot_UnreachableAndFindResource_AreExplicit()
    {
        var snapshot = TopologySnapshot.Unreachable(TopologyProfile.LoadTests, DateTimeOffset.UnixEpoch, "transport");

        snapshot.IsReachable.Should().BeFalse();
        snapshot.FindResource("missing").Should().BeNull();
    }

    [Fact]
    public void Parse_MixedReplicaStates_AggregatesAsDegradedAndIsNotReady()
    {
        const string json =
            """
            {
              "resources": [
                { "name":"postgres-x", "displayName":"postgres", "resourceType":"Container", "state":"Running", "healthStatus":"Healthy" },
                { "name":"redis-x", "displayName":"redis", "resourceType":"Container", "state":"Running", "healthStatus":"Healthy" },
                { "name":"jaeger-x", "displayName":"jaeger", "resourceType":"Container", "state":"Running", "healthStatus":"Healthy" },
                { "name":"core-1", "displayName":"corebank-api", "resourceType":"Project", "state":"Running", "healthStatus":"Healthy" },
                { "name":"core-2", "displayName":"corebank-api", "resourceType":"Project", "state":"Stopped" },
                { "name":"payments-1", "displayName":"payments-api", "resourceType":"Project", "state":"Running", "healthStatus":"Healthy" }
              ]
            }
            """;

        var snapshot = AspireJsonParser.Parse(TopologyProfile.Regular, json, DateTimeOffset.UnixEpoch);

        snapshot.FindResource(KnownResources.CoreBankApi)!.Condition.Should().Be(ResourceCondition.Degraded);
        snapshot.IsReady.Should().BeFalse();
    }

    [Fact]
    public void Parse_NonZeroOneShotExit_IsFailed()
    {
        const string json =
            """
            {
              "resources": [
                { "name":"postgres", "state":"Running", "healthStatus":"Healthy" },
                { "name":"redis", "state":"Running", "healthStatus":"Healthy" },
                { "name":"jaeger", "state":"Running", "healthStatus":"Healthy" },
                { "name":"corebank-api", "resourceType":"Project", "state":"Running", "healthStatus":"Healthy" },
                { "name":"payments-api", "resourceType":"Project", "state":"Running", "healthStatus":"Healthy" },
                { "name":"loadtest-support", "resourceType":"Project", "state":"Running", "healthStatus":"Healthy" },
                { "name":"loadtest-initializer", "resourceType":"Project", "state":"Finished", "properties":{"executable.exitCode":0} },
                { "name":"k6", "state":"Exited", "properties":{"container.exitCode":1} }
              ]
            }
            """;

        var snapshot = AspireJsonParser.Parse(TopologyProfile.LoadTests, json, DateTimeOffset.UnixEpoch);

        snapshot.FindResource(KnownResources.K6)!.Condition.Should().Be(ResourceCondition.Failed);
        snapshot.IsReady.Should().BeFalse();
    }

    private static string RequiredJson(string coreBankState, string coreBankHealth) =>
        $$"""
          {
            "items": [
              { "resourceName": "postgres", "resourceState": "Running", "health": "Healthy" },
              { "resourceName": "redis", "resourceState": "Running", "health": "Healthy" },
              { "resourceName": "jaeger", "resourceState": "Running", "health": "Healthy" },
              { "resourceName": "corebank-api", "resourceState": "{{coreBankState}}", "health": "{{coreBankHealth}}" },
              { "resourceName": "payments-api", "resourceState": "Running", "health": "Healthy" }
            ]
          }
          """;
}
