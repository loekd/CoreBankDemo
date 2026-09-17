using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoreBankDemo.ServiceDefaults.Tests.Dashboards;

/// <summary>
/// Shared helpers for the provisioned LGTM dashboard drift tests: collect every query
/// expression in a dashboard and map <c>corebankdemo_*</c> Prometheus metric names back to
/// <see cref="BusinessMetrics"/> instrument names under the OTLP-to-Prometheus translation
/// ("." becomes "_", counters gain "_total", millisecond histograms gain
/// "_milliseconds" plus "_bucket"/"_count"/"_sum").
/// </summary>
internal static class DashboardQueries
{
    public static readonly Regex MetricReference = new(@"corebankdemo_[a-z0-9_]+", RegexOptions.Compiled);

    private static readonly string[] PrometheusSuffixes =
        ["_milliseconds_bucket", "_milliseconds_count", "_milliseconds_sum", "_total"];

    public static HashSet<string> InstrumentNames() =>
        typeof(BusinessMetrics)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.Name.EndsWith("InstrumentName", StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

    public static IEnumerable<string> ExpressionsIn(string dashboardJson)
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

    public static string ToInstrumentName(string prometheusMetric)
    {
        var suffix = PrometheusSuffixes.FirstOrDefault(s => prometheusMetric.EndsWith(s, StringComparison.Ordinal));
        var stem = suffix is null ? prometheusMetric : prometheusMetric[..^suffix.Length];
        return stem.Replace('_', '.');
    }
}
