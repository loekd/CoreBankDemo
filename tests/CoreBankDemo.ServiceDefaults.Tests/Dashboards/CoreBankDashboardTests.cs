using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.Dashboards;

/// <summary>
/// ADR-022 drift test for the provisioned LGTM dashboard
/// (<c>observability/grafana/dashboards/corebank.json</c>, linked into this project's
/// output). The uid is a contract DemoRunner deep-links to, and every
/// <c>corebankdemo_*</c> metric a panel queries must map back to a
/// <see cref="BusinessMetrics"/> instrument name (see <see cref="DashboardQueries"/>), so
/// renaming an instrument fails the build instead of blanking a panel.
/// </summary>
public class CoreBankDashboardTests
{
    private static readonly string DashboardPath =
        Path.Combine(AppContext.BaseDirectory, "Dashboards", "corebank.json");

    [Fact]
    public void Dashboard_uid_is_the_corebank_contract()
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));

        dashboard.RootElement.GetProperty("uid").GetString().Should().Be("corebank",
            "DemoRunner deep-links to http://localhost:3000/d/corebank");
    }

    [Fact]
    public void Dashboard_links_to_the_traces_dashboard()
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));

        dashboard.RootElement.GetProperty("links").EnumerateArray()
            .Select(link => link.TryGetProperty("url", out var url) ? url.GetString() : null)
            .Should().Contain("/d/corebank-traces", "the detailed dashboard offers a shortcut to the trace search");
    }

    [Theory]
    [InlineData("corebankdemo_messaging_queue_duration_milliseconds_bucket", BusinessMetrics.MessagingQueueDurationInstrumentName)]
    [InlineData("corebankdemo_transaction_execution_duration_milliseconds_bucket", BusinessMetrics.TransactionExecutionDurationInstrumentName)]
    [InlineData("corebankdemo_transaction_execution_duration_milliseconds_count", BusinessMetrics.TransactionExecutionDurationInstrumentName)]
    [InlineData("corebankdemo_transaction_processed_total", BusinessMetrics.TransactionProcessedInstrumentName)]
    public void Prometheus_metric_names_translate_back_to_their_BusinessMetrics_instrument(string prometheusMetric, string instrumentName)
    {
        // The OTLP-to-Prometheus translation the drift test relies on: "." to
        // "_", "_total" on counters, "_milliseconds" plus the histogram series
        // suffix on the millisecond histograms (queue, instant-payment and
        // transaction-execution duration).
        DashboardQueries.ToInstrumentName(prometheusMetric).Should().Be(instrumentName);
        DashboardQueries.InstrumentNames().Should().Contain(instrumentName);
    }

    [Fact]
    public void Throughput_and_latency_row_reads_processed_rate_then_inbox_wait_then_execution_time()
    {
        // The regression-analysis trio: how much got done, how long rows
        // waited for a worker, how long the work itself took once picked up.
        // Queue duration alone cannot tell a slow executor (lock contention)
        // from a starved one, so the third panel is the new histogram.
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));
        var panels = dashboard.RootElement.GetProperty("panels").EnumerateArray().ToList();
        var row = panels.Should()
            .ContainSingle(panel => panel.GetProperty("title").GetString() == "CoreBank throughput and latency").Subject;
        var rowY = row.GetProperty("gridPos").GetProperty("y").GetInt32();

        var rowPanels = panels
            .Where(panel => panel.GetProperty("type").GetString() == "timeseries"
                            && panel.GetProperty("gridPos").GetProperty("y").GetInt32() == rowY + 1)
            .OrderBy(panel => panel.GetProperty("gridPos").GetProperty("x").GetInt32())
            .ToList();

        rowPanels.Select(panel => panel.GetProperty("title").GetString()).Should().Equal(
            ["Transactions processed/s", "corebank-inbox queue duration p95", "Execution duration p95"]);
        rowPanels.Select(panel => panel.GetProperty("targets").EnumerateArray().Single().GetProperty("expr").GetString()).Should().Equal(
        [
            "sum(rate(corebankdemo_transaction_processed_total{service_name=~\"$service\"}[$__rate_interval]))",
            "histogram_quantile(0.95, sum by (le) (rate(corebankdemo_messaging_queue_duration_milliseconds_bucket{messaging_store_name=\"corebank-inbox\", service_name=~\"$service\"}[$__rate_interval])))",
            "histogram_quantile(0.95, sum by (le, outcome) (rate(corebankdemo_transaction_execution_duration_milliseconds_bucket{service_name=~\"$service\"}[$__rate_interval])))",
        ]);
        rowPanels.Select(panel => panel.GetProperty("fieldConfig").GetProperty("defaults").GetProperty("unit").GetString())
            .Should().Equal(["ops", "ms", "ms"]);
    }

    [Fact]
    public void Every_corebankdemo_metric_the_dashboard_queries_is_a_BusinessMetrics_instrument()
    {
        var instrumentNames = DashboardQueries.InstrumentNames();
        var referenced = DashboardQueries.ExpressionsIn(File.ReadAllText(DashboardPath))
            .SelectMany(expr => DashboardQueries.MetricReference.Matches(expr).Select(match => match.Value))
            .Distinct()
            .ToList();

        referenced.Should().NotBeEmpty("the dashboard is expected to chart the business meter");
        foreach (var metric in referenced)
        {
            DashboardQueries.ToInstrumentName(metric).Should().BeOneOf(instrumentNames,
                $"dashboard query metric '{metric}' must translate to a BusinessMetrics instrument");
        }
    }
}
