using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.Dashboards;

/// <summary>
/// Drift test for the provisioned LGTM traces dashboard
/// (<c>observability/grafana/dashboards/corebank-traces.json</c>), the Jaeger-style
/// "service + operation" trace search both AppHosts set as the Grafana home page.
/// The AppHost sources are linked into this project's output so a renamed dashboard
/// file, or one AppHost drifting from the other, fails the build instead of landing
/// the audience on an empty home page.
/// </summary>
public class CoreBankTracesDashboardTests
{
    private const string ProvisionedDashboardDirectory = "/otel-lgtm/grafana/conf/provisioning/dashboards/custom";

    private static readonly string DashboardPath =
        Path.Combine(AppContext.BaseDirectory, "Dashboards", "corebank-traces.json");

    private static readonly Regex HomeDashboardPath = new(
        @"WithEnvironment\(""GF_DASHBOARDS_DEFAULT_HOME_DASHBOARD_PATH"", ""(?<path>[^""]+)""\)",
        RegexOptions.Compiled);

    [Fact]
    public void Dashboard_uid_is_the_corebank_traces_contract()
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));

        dashboard.RootElement.GetProperty("uid").GetString().Should().Be("corebank-traces",
            "the CoreBank dashboard links to http://localhost:3000/d/corebank-traces");
    }

    [Fact]
    public void Every_trace_and_span_metric_query_filters_on_the_selected_service_and_operation()
    {
        var queries = PanelQueries()
            .Where(query => !DashboardQueries.MetricReference.IsMatch(query))
            .ToList();

        queries.Should().NotBeEmpty("the dashboard is expected to search traces and chart span metrics");
        foreach (var query in queries)
        {
            query.Should().Contain("$service").And.Contain("$operation",
                "every trace list and span-metric chart follows the Service and Operation filters");
        }
    }

    [Fact]
    public void Every_BusinessMetrics_instrument_is_charted()
    {
        var charted = PanelQueries()
            .SelectMany(expr => DashboardQueries.MetricReference.Matches(expr).Select(match => match.Value))
            .Select(DashboardQueries.ToInstrumentName)
            .ToHashSet(StringComparer.Ordinal);

        charted.Should().BeEquivalentTo(DashboardQueries.InstrumentNames(),
            "the custom metrics section charts every instrument on the business meter, and nothing that is not one");
    }

    [Fact]
    public void Every_custom_metric_query_filters_on_each_tag_it_splits_by()
    {
        var queries = PanelQueries()
            .Where(query => DashboardQueries.MetricReference.IsMatch(query))
            .ToList();

        queries.Should().NotBeEmpty();
        foreach (var query in queries)
        {
            var tags = SplitBy.Match(query).Groups["labels"].Value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(label => label != "le")
                .ToList();

            tags.Should().NotBeEmpty($"'{query}' splits its series by the instrument's tags");
            foreach (var tag in tags)
            {
                TagFilters.Should().ContainKey(tag, $"'{query}' splits by '{tag}', which needs a filter");
                query.Should().Contain($"{tag}=~\"${TagFilters[tag]}\"",
                    $"the '{TagFilters[tag]}' filter applies to every panel split by '{tag}'");
            }
        }
    }

    [Fact]
    public void Every_tag_filter_is_an_optional_multi_select()
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));
        var variables = dashboard.RootElement.GetProperty("templating").GetProperty("list").EnumerateArray()
            .ToDictionary(variable => variable.GetProperty("name").GetString()!);

        foreach (var name in TagFilters.Values)
        {
            variables.Should().ContainKey(name);
            var variable = variables[name];
            variable.GetProperty("multi").GetBoolean().Should().BeTrue($"'{name}' can select several values");
            variable.GetProperty("includeAll").GetBoolean().Should().BeTrue($"'{name}' is optional");
            variable.GetProperty("allValue").GetString().Should().Be(".*",
                $"'{name}' left on All also matches series without that tag");
            variable.GetProperty("current").GetProperty("value").EnumerateArray().Select(value => value.GetString())
                .Should().Equal(["$__all"], $"'{name}' starts unfiltered");
        }
    }

    [Fact]
    public void Service_map_query_is_rendered_by_the_node_graph_panel()
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));

        var serviceMapPanels = dashboard.RootElement.GetProperty("panels").EnumerateArray()
            .Where(panel => panel.TryGetProperty("targets", out var targets) && targets.EnumerateArray().Any(IsServiceMap))
            .Select(panel => panel.GetProperty("type").GetString())
            .ToList();

        // Grafana panel plugin ids are case-sensitive: "nodegraph" renders "plugin not found".
        serviceMapPanels.Should().ContainSingle().Which.Should().Be("nodeGraph");
    }

    [Theory]
    [InlineData("CoreBankDemo.AppHost")]
    [InlineData("CoreBankDemo.LoadTests")]
    public void AppHost_opens_Grafana_on_the_traces_dashboard(string appHost)
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AppHosts", appHost, "AppHost.cs"));

        var match = HomeDashboardPath.Match(source);

        match.Success.Should().BeTrue($"{appHost} must set the Grafana home dashboard");
        match.Groups["path"].Value.Should().Be($"{ProvisionedDashboardDirectory}/{Path.GetFileName(DashboardPath)}",
            "the home dashboard is the provisioned traces dashboard file inside the container");
    }

    private static readonly Regex SplitBy = new(@"by \((?<labels>[^)]*)\)", RegexOptions.Compiled);

    /// <summary>Metric tag (Prometheus label) to the dashboard variable that filters it.</summary>
    private static readonly Dictionary<string, string> TagFilters = new(StringComparer.Ordinal)
    {
        ["outcome"] = "outcome",
        ["payment_scheme"] = "payment_scheme",
        ["messaging_store_name"] = "store_name",
        ["messaging_store_kind"] = "store_kind",
        ["messaging_direction"] = "direction",
        ["messaging_message_type"] = "message_type",
        ["messaging_transport"] = "transport",
    };

    /// <summary>The PromQL and TraceQL of every panel target, leaving out the service map query.</summary>
    private static List<string> PanelQueries()
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));
        return dashboard.RootElement.GetProperty("panels").EnumerateArray()
            .Where(panel => panel.TryGetProperty("targets", out _))
            .SelectMany(panel => panel.GetProperty("targets").EnumerateArray())
            .Where(target => !IsServiceMap(target))
            .Select(QueryOf)
            .ToList();
    }

    private static bool IsServiceMap(JsonElement target) =>
        target.TryGetProperty("queryType", out var queryType) && queryType.GetString() == "serviceMap";

    private static string QueryOf(JsonElement target) =>
        target.TryGetProperty("expr", out var expr) ? expr.GetString()! : target.GetProperty("query").GetString()!;
}
