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
    private const string FailedPaymentsTable = "Which payment failed, and why";

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
        var queries = PanelQueries(except: FailedPaymentsTable)
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
    public void Failed_payments_table_lists_every_cancelled_or_rejected_payment_with_its_reason()
    {
        // "Which payment failed, and why": PaymentsAPI tags the span where a
        // payment provably ends cancelled or rejected (FailedPaymentTags), so
        // this table needs no service or operation filter and never lists a
        // cancel that lost its race to execution.
        var panel = Panel(FailedPaymentsTable);

        panel.GetProperty("type").GetString().Should().Be("table");
        var target = panel.GetProperty("targets").EnumerateArray().Should().ContainSingle().Subject;
        target.GetProperty("queryType").GetString().Should().Be("traceql");
        target.GetProperty("tableType").GetString().Should().Be("spans",
            "one row per failed payment, with the tagged attributes as columns and the trace ID as the link");
        target.GetProperty("query").GetString().Should().Be(
            "{ span.payment.outcome =~ \"cancelled|rejected\" } | select(span.transaction.id, span.payment.outcome, span.payment.failure_reason)");
    }

    [Fact]
    public void Today_row_shows_the_four_flow_totals_in_flow_order()
    {
        var titles = DailyTotals()
            .OrderBy(panel => panel.GetProperty("gridPos").GetProperty("x").GetInt32())
            .Select(panel => panel.GetProperty("title").GetString())
            .ToList();

        titles.Should().Equal(["Payments received", "Transactions processed", "Messages sent", "Failed payments"],
            "the row reads left to right as the payment flows: stored in the Payments outbox, executed once, result event published, and the ones that did not settle");
    }

    [Fact]
    public void Failed_payments_total_adds_cancelled_outbox_rows_to_business_rejections()
    {
        // The same "since midnight" arithmetic as the other tiles, once per
        // source: a cancelled payments-outbox row (the only place every kind
        // of cancel is counted) plus CoreBank's business rejections.
        var panel = DailyTotals().Should()
            .ContainSingle(candidate => candidate.GetProperty("title").GetString() == "Failed payments").Subject;

        panel.GetProperty("type").GetString().Should().Be("stat");
        panel.GetProperty("timeFrom").GetString().Should().Be("now/d");
        panel.GetProperty("targets").EnumerateArray().Select(QueryOf).Should().Equal([
            $"({SinceMidnight("corebankdemo_messaging_items_processed_total{service_name=\"CoreBank.PaymentsAPI\", messaging_store_name=\"payments-outbox\", outcome=\"cancelled\"}")}) + ({SinceMidnight("corebankdemo_transaction_processed_total{service_name=\"CoreBank.CoreBankAPI\", outcome=\"business_rejected\"}")})"],
            "each source falls back to 0 on its own, so a day with no rejections still shows its cancellations");
    }

    [Theory]
    [InlineData("Payments received", "corebankdemo_payment_intake_total{service_name=\"CoreBank.PaymentsAPI\", outcome=\"stored\"}")]
    [InlineData("Transactions processed", "corebankdemo_transaction_processed_total{service_name=\"CoreBank.CoreBankAPI\"}")]
    [InlineData("Messages sent", "corebankdemo_messaging_deliveries_total{service_name=\"CoreBank.CoreBankAPI\", messaging_direction=\"sent\", outcome=\"succeeded\", messaging_message_type=~\"transaction-completed|transaction-failed\"}")]
    public void Daily_total_counts_one_per_payment_since_midnight(string title, string selector)
    {
        var panel = DailyTotals().Should()
            .ContainSingle(candidate => candidate.GetProperty("title").GetString() == title).Subject;

        panel.GetProperty("type").GetString().Should().Be("stat", "a daily total is one big number, not a chart");
        panel.GetProperty("timeFrom").GetString().Should().Be("now/d",
            "the tile counts today so far while the trace panels keep the dashboard's own range");
        panel.GetProperty("targets").EnumerateArray().Select(QueryOf).Should()
            .Equal([SinceMidnight(selector)],
                "each tile is one exact count that lines up with its neighbours unless there is an outage");
    }

    /// <summary>
    /// Scoped to the service that owns the number: any other process exporting to the same
    /// collector (a test run's "test-service", say) must not move a tile.
    /// Not increase(): it skips a series' first sample, and a business counter first
    /// appears already counting, so increase() under-reports and the tiles stop lining up.
    /// Every process is its own service_instance_id series that never resets, so the last
    /// value each reported today, less what it already had at midnight, is exact.
    /// </summary>
    private static string SinceMidnight(string selector)
    {
        var today = $"last_over_time({selector}[$__range])";
        return $"sum(({today} - ({selector} offset $__range)) or {today}) or vector(0)";
    }

    [Fact]
    public void Every_custom_metric_queried_is_a_BusinessMetrics_instrument()
    {
        var queried = PanelQueries()
            .SelectMany(expr => DashboardQueries.MetricReference.Matches(expr).Select(match => match.Value))
            .Select(DashboardQueries.ToInstrumentName)
            .ToList();

        queried.Should().NotBeEmpty();
        queried.Should().BeSubsetOf(DashboardQueries.InstrumentNames(),
            "a renamed instrument must fail the build instead of leaving a tile at 0 on stage");
    }

    [Fact]
    public void Only_the_trace_filters_remain()
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));

        var variables = dashboard.RootElement.GetProperty("templating").GetProperty("list").EnumerateArray()
            .Select(variable => variable.GetProperty("name").GetString());

        variables.Should().Equal(["service", "operation"], "the daily totals take no filters");
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

    /// <summary>Every panel that queries the business meter.</summary>
    private static List<JsonElement> DailyTotals()
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));
        return dashboard.RootElement.GetProperty("panels").EnumerateArray()
            .Where(panel => panel.TryGetProperty("targets", out var targets)
                && targets.EnumerateArray().Any(target => !IsServiceMap(target) && DashboardQueries.MetricReference.IsMatch(QueryOf(target))))
            .Select(panel => panel.Clone())
            .ToList();
    }

    /// <summary>The PromQL and TraceQL of every panel target, leaving out the service map query.</summary>
    private static List<string> PanelQueries(string? except = null)
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));
        return dashboard.RootElement.GetProperty("panels").EnumerateArray()
            .Where(panel => panel.TryGetProperty("targets", out _) && panel.GetProperty("title").GetString() != except)
            .SelectMany(panel => panel.GetProperty("targets").EnumerateArray())
            .Where(target => !IsServiceMap(target))
            .Select(QueryOf)
            .ToList();
    }

    private static JsonElement Panel(string title)
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));
        return dashboard.RootElement.GetProperty("panels").EnumerateArray()
            .Should().ContainSingle(panel => panel.GetProperty("title").GetString() == title).Subject.Clone();
    }

    private static bool IsServiceMap(JsonElement target) =>
        target.TryGetProperty("queryType", out var queryType) && queryType.GetString() == "serviceMap";

    private static string QueryOf(JsonElement target) =>
        target.TryGetProperty("expr", out var expr) ? expr.GetString()! : target.GetProperty("query").GetString()!;
}
