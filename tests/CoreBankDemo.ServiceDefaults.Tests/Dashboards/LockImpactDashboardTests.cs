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
/// and Tempo keeps it on every span (as <c>resource.run.id</c>). Both AppHost sources are
/// linked into this project's output, so a renamed attribute, a dropped stamp, or a stamp
/// leaking into the Regular AppHost fails the build instead of emptying the dropdown.
/// </summary>
public class LockImpactDashboardTests
{
    private const string ResourceAttribute = "run.id";
    private const string PrometheusLabel = "run_id";
    private const string ThroughputTile = "Throughput (peak)";
    private const string LockWaitsTile = "Advisory-lock waits";
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
            .Should().Equal([(ThroughputTile, "stat"), (LockWaitsTile, "stat"), (ThroughputChart, "timeseries")]);
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
    public void Lock_waits_tile_counts_the_advisory_lock_spans_of_the_selected_run_from_tempo()
    {
        // Every Npgsql span is named "postgresql", so Tempo's span metrics cannot single out
        // the lock; the SQL text on the span can, and resource.run.id scopes it to the run.
        var panel = Panel(LockWaitsTile);
        var target = panel.GetProperty("targets").EnumerateArray().Should().ContainSingle().Subject;

        target.GetProperty("datasource").GetProperty("type").GetString().Should().Be("tempo");
        target.GetProperty("queryType").GetString().Should().Be("traceql");
        var query = target.GetProperty("query").GetString()!;
        query.Should().StartWith($"{{ resource.{ResourceAttribute} = \"$run\"");
        query.Should().Contain("span.db.query.text =~ \".*pg_advisory_xact_lock.*\"");
        query.Should().EndWith("| count_over_time()", "a TraceQL metrics query, summed over the range by the tile");

        var reduce = panel.GetProperty("options").GetProperty("reduceOptions");
        reduce.GetProperty("calcs").EnumerateArray().Select(calc => calc.GetString()).Should().Equal(["sum"]);
        reduce.GetProperty("fields").GetString().Should().Be("/^count_over_time$/",
            "Tempo also returns an exemplar frame per step that must not be summed into the count");

        var steps = panel.GetProperty("fieldConfig").GetProperty("defaults").GetProperty("thresholds").GetProperty("steps")
            .EnumerateArray().Select(step => (step.GetProperty("color").GetString(), step.GetProperty("value")))
            .ToList();
        steps.Select(step => step.Item1).Should().Equal(["green", "red"]);
        steps[1].Item2.GetInt32().Should().Be(1, "one lock wait is already the wrong build");
        panel.GetProperty("fieldConfig").GetProperty("defaults").GetProperty("noValue").GetString().Should().Be("0",
            "a run with no lock spans at all must read 0, not No data");
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
            "Tempo caps metrics queries at 3 h, so the default range must not exceed it");
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
