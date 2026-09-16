using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.Dashboards;

/// <summary>
/// ADR-022 drift test for the provisioned LGTM dashboard
/// (<c>observability/grafana/dashboards/corebank.json</c>, linked into this project's
/// output). The uid is a contract DemoRunner deep-links to, and every
/// <c>corebankdemo_*</c> metric a panel queries must map back to a
/// <see cref="BusinessMetrics"/> instrument name under the OTLP-to-Prometheus translation
/// ("." becomes "_", counters gain "_total", millisecond histograms gain
/// "_milliseconds" plus "_bucket"/"_count"/"_sum"), so renaming an instrument fails
/// the build instead of blanking a panel.
/// </summary>
public class CoreBankDashboardTests
{
    private static readonly string DashboardPath =
        Path.Combine(AppContext.BaseDirectory, "Dashboards", "corebank.json");

    private static readonly Regex MetricReference = new(@"corebankdemo_[a-z0-9_]+", RegexOptions.Compiled);

    private static readonly string[] PrometheusSuffixes =
        ["_milliseconds_bucket", "_milliseconds_count", "_milliseconds_sum", "_total"];

    [Fact]
    public void Dashboard_uid_is_the_corebank_contract()
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(DashboardPath));

        dashboard.RootElement.GetProperty("uid").GetString().Should().Be("corebank",
            "DemoRunner deep-links to http://localhost:3000/d/corebank");
    }

    [Fact]
    public void Every_corebankdemo_metric_the_dashboard_queries_is_a_BusinessMetrics_instrument()
    {
        var instrumentNames = typeof(BusinessMetrics)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.Name.EndsWith("InstrumentName", StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
        var referenced = ExpressionsIn(File.ReadAllText(DashboardPath))
            .SelectMany(expr => MetricReference.Matches(expr).Select(match => match.Value))
            .Distinct()
            .ToList();

        referenced.Should().NotBeEmpty("the dashboard is expected to chart the business meter");
        foreach (var metric in referenced)
        {
            ToInstrumentName(metric).Should().BeOneOf(instrumentNames,
                $"dashboard query metric '{metric}' must translate to a BusinessMetrics instrument");
        }
    }

    private static IEnumerable<string> ExpressionsIn(string dashboardJson)
    {
        using var dashboard = JsonDocument.Parse(dashboardJson);
        var expressions = new List<string>();
        Collect(dashboard.RootElement, expressions);
        return expressions;

        static void Collect(JsonElement element, List<string> expressions)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name is "expr" or "query" && property.Value.ValueKind == JsonValueKind.String)
                        {
                            expressions.Add(property.Value.GetString()!);
                        }
                        else
                        {
                            Collect(property.Value, expressions);
                        }
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Collect(item, expressions);
                    }

                    break;
            }
        }
    }

    private static string ToInstrumentName(string prometheusMetric)
    {
        var suffix = PrometheusSuffixes.FirstOrDefault(s => prometheusMetric.EndsWith(s, StringComparison.Ordinal));
        var stem = suffix is null ? prometheusMetric : prometheusMetric[..^suffix.Length];
        return stem.Replace('_', '.');
    }
}
