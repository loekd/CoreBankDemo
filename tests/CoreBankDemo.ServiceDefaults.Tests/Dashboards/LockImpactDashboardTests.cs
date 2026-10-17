using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.Dashboards;

/// <summary>
/// Drift test for the provisioned lock-impact dashboard
/// (<c>observability/grafana/dashboards/lock-impact.json</c>): one load-test run at a
/// time, picked by the <c>run</c> variable. The run identity is the OTel resource
/// attribute <c>run.id</c> the LoadTests AppHost stamps on both APIs; Prometheus keeps it
/// on <c>target_info</c> only (as <c>run_id</c>), so every metric query must join it on,
/// including the execution p95 tile. Both AppHost sources are
/// linked into this project's output, so a renamed attribute, a dropped stamp, or a stamp
/// leaking into the Regular AppHost fails the build instead of emptying the dropdown.
/// </summary>
public class LockImpactDashboardTests
{
    private const string ResourceAttribute = "run.id";
    private const string PrometheusLabel = "run_id";
    private const string ThroughputTile = "Throughput (peak)";
    private const string ExecutionTile = "Execution p95";
    private const string ThroughputChart = "Transactions processed / s";

    private static readonly string DashboardPath =
        Path.Combine(AppContext.BaseDirectory, "Dashboards", "lock-impact.json");

    private static readonly Regex RunIdStamp = new(@"run\.id=\{runId\}", RegexOptions.Compiled);

    [Fact]
    public void Dashboard_uid_is_the_lock_impact_contract()
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));

        dashboard.RootElement.GetProperty("uid").GetString().Should().Be("lock-impact",
            "the kiosk URL on stage is http://localhost:3000/d/lock-impact");
    }

    [Fact]
    public void Run_variable_lists_run_ids_newest_first_and_selects_exactly_one()
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));
        var variable = dashboard.RootElement.GetProperty("templating").GetProperty("list").EnumerateArray()
            .Should().ContainSingle("one run at a time is the whole point").Subject;

        variable.GetProperty("name").GetString().Should().Be("run");
        variable.GetProperty("type").GetString().Should().Be("query");
        variable.GetProperty("definition").GetString().Should().Be(
            $"label_values(target_info{{{PrometheusLabel}!=\"\"}}, {PrometheusLabel})",
            "run ids live on target_info only; the Regular AppHost's services carry none and must not add an empty entry");
        variable.GetProperty("sort").GetInt32().Should().Be(2, "alphabetical descending puts the newest timestamp first");
        variable.GetProperty("includeAll").GetBoolean().Should().BeFalse();
        variable.GetProperty("multi").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Panels_are_the_two_tiles_above_the_full_width_chart()
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));
        var panels = dashboard.RootElement.GetProperty("panels").EnumerateArray()
            .OrderBy(panel => panel.GetProperty("gridPos").GetProperty("y").GetInt32())
            .ThenBy(panel => panel.GetProperty("gridPos").GetProperty("x").GetInt32())
            .ToList();

        panels.Select(panel => (panel.GetProperty("title").GetString(), panel.GetProperty("type").GetString()))
            .Should().Equal([(ThroughputTile, "stat"), (ExecutionTile, "stat"), (ThroughputChart, "timeseries")]);
        panels[2].GetProperty("gridPos").GetProperty("w").GetInt32().Should().Be(24, "the chart spans the full width");
    }

    [Fact]
    public void Every_prometheus_query_joins_the_run_id_from_target_info()
    {
        var queries = PrometheusQueries();

        queries.Should().NotBeEmpty();
        foreach (var query in queries)
        {
            query.Should().Contain($"* on(job, instance) group_left({PrometheusLabel}) target_info{{{PrometheusLabel}=\"$run\"}}",
                "run.id is not a promoted resource attribute, so it reaches the series only through this join");
        }
    }

    [Fact]
    public void Every_prometheus_query_steps_finer_than_a_short_run()
    {
        // The LGTM image provisions its Prometheus datasource with a 60 s minimum step; a
        // run that lasts half a minute then falls between two grid points and reads 0.
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));
        var targets = dashboard.RootElement.GetProperty("panels").EnumerateArray()
            .SelectMany(panel => panel.GetProperty("targets").EnumerateArray())
            .Where(target => target.GetProperty("datasource").GetProperty("type").GetString() == "prometheus")
            .ToList();

        targets.Should().NotBeEmpty();
        foreach (var target in targets)
        {
            target.GetProperty("interval").GetString().Should().Be("5s");
        }
    }

    [Fact]
    public void Every_custom_metric_queried_is_a_BusinessMetrics_instrument()
    {
        var queried = PrometheusQueries()
            .SelectMany(expr => DashboardQueries.MetricReference.Matches(expr).Select(match => match.Value))
            .Select(DashboardQueries.ToInstrumentName)
            .ToList();

        queried.Should().NotBeEmpty();
        queried.Should().BeSubsetOf(DashboardQueries.InstrumentNames(),
            "a renamed instrument must fail the build instead of leaving a tile at 0 on stage");
    }

    [Fact]
    public void Execution_p95_tile_is_the_runs_own_quantile_of_the_execution_histogram()
    {
        // A lock held across publishing is waited for inside the executor call, so the
        // transaction.execution.duration histogram is where the plant shows: ~5 ms on the
        // default build, hundreds of ms with Demo:LockRelease = afterPublish. The counters
        // start at zero with each run's processes, so the series' last point is the run's p95.
        var panel = Panel(ExecutionTile);
        var target = panel.GetProperty("targets").EnumerateArray().Should().ContainSingle().Subject;

        target.GetProperty("datasource").GetProperty("type").GetString().Should().Be("prometheus");
        var expr = QueryOf(target);
        expr.Should().StartWith("histogram_quantile(0.95, sum by (le) (corebankdemo_transaction_execution_duration_milliseconds_bucket{service_name=\"CoreBank.CoreBankAPI\"}");
        target.GetProperty("range").GetBoolean().Should().BeTrue("the join needs target_info at each step of the run, not at the instant the dashboard is viewed");

        panel.GetProperty("options").GetProperty("reduceOptions").GetProperty("calcs").EnumerateArray()
            .Select(calc => calc.GetString()).Should().Equal(["lastNotNull"]);
        panel.GetProperty("fieldConfig").GetProperty("defaults").GetProperty("unit").GetString().Should().Be("ms");
        var steps = panel.GetProperty("fieldConfig").GetProperty("defaults").GetProperty("thresholds").GetProperty("steps")
            .EnumerateArray().Select(step => step.GetProperty("color").GetString()).ToList();
        steps.Should().Equal(["green", "red"], "a healthy executor is green; a held lock turns the tile red");
    }

    [Fact]
    public void Throughput_tile_is_the_peak_of_the_chart_series()
    {
        var tile = Panel(ThroughputTile);
        var chart = Panel(ThroughputChart);

        tile.GetProperty("options").GetProperty("reduceOptions").GetProperty("calcs").EnumerateArray()
            .Select(calc => calc.GetString()).Should().Equal(["max"]);
        tile.GetProperty("fieldConfig").GetProperty("defaults").GetProperty("unit").GetString().Should().Be("suffix: payments/s");
        tile.GetProperty("options").GetProperty("text").GetProperty("valueSize").GetInt32().Should().BeGreaterThanOrEqualTo(32);
        QueryOf(tile.GetProperty("targets").EnumerateArray().Single())
            .Should().Be(QueryOf(chart.GetProperty("targets").EnumerateArray().Single()),
                "the number on the tile is the highest point of the chart below it");
    }

    [Fact]
    public void Chart_is_one_line_from_zero_with_no_legend()
    {
        var chart = Panel(ThroughputChart);
        var defaults = chart.GetProperty("fieldConfig").GetProperty("defaults");

        defaults.GetProperty("min").GetInt32().Should().Be(0);
        defaults.GetProperty("custom").GetProperty("lineWidth").GetDouble().Should().Be(2.5);
        chart.GetProperty("options").GetProperty("legend").GetProperty("showLegend").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Dashboard_opens_on_the_last_three_hours_refreshing_every_five_seconds()
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));

        dashboard.RootElement.GetProperty("time").GetProperty("from").GetString().Should().Be("now-3h",
            "a rehearsal's runs are minutes apart, so three hours keeps the dropdown to the runs that matter");
        dashboard.RootElement.GetProperty("refresh").GetString().Should().Be("5s");
        dashboard.RootElement.GetProperty("style").GetString().Should().Be("dark");
    }

    [Fact]
    public void Prometheus_label_is_the_resource_attribute_with_dots_replaced()
    {
        // The dashboard description documents both spellings; this pins that they are
        // the same attribute under the OTLP-to-Prometheus translation.
        ResourceAttribute.Replace('.', '_').Should().Be(PrometheusLabel);
    }

    [Fact]
    public void LoadTests_AppHost_appends_the_run_id_stamp_to_both_apis()
    {
        var source = AppHostSource("CoreBankDemo.LoadTests");

        RunIdStamp.IsMatch(source).Should().BeTrue("the LoadTests AppHost stamps run.id on its services");
        source.Should().Contain("StampRunId(coreBankApi);").And.Contain("StampRunId(paymentsApi);");
        source.Should().Contain("TryGetValue(\"OTEL_RESOURCE_ATTRIBUTES\"",
            "Aspire sets OTEL_RESOURCE_ATTRIBUTES itself (service.instance.id); the stamp must append, never replace");
    }

    [Fact]
    public void Regular_AppHost_carries_no_run_id_stamp()
    {
        AppHostSource("CoreBankDemo.AppHost").Should().NotContain(ResourceAttribute,
            "the run identity is load-test infrastructure only (ADR-016 tiers); application code and the Regular AppHost stay untouched");
    }

    private static string AppHostSource(string appHost) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AppHosts", appHost, "AppHost.cs"));

    private static List<string> PrometheusQueries()
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));
        return dashboard.RootElement.GetProperty("panels").EnumerateArray()
            .SelectMany(panel => panel.GetProperty("targets").EnumerateArray())
            .Where(target => target.GetProperty("datasource").GetProperty("type").GetString() == "prometheus")
            .Select(QueryOf)
            .ToList();
    }

    private static JsonElement Panel(string title)
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));
        return dashboard.RootElement.GetProperty("panels").EnumerateArray()
            .Should().ContainSingle(panel => panel.GetProperty("title").GetString() == title).Subject.Clone();
    }

    private static string QueryOf(JsonElement target) =>
        target.TryGetProperty("expr", out var expr) ? expr.GetString()! : target.GetProperty("query").GetString()!;
}
