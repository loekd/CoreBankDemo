using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.Lgtm;

/// <summary>
/// Drift tests for the LGTM data-volume decisions (spec: lgtm-data-volume): one-day
/// retention in the component configs both AppHosts mount over the image's, and no
/// per-tick logging from the services. Sources and configs are linked into this
/// project's output so a renamed file or a dropped setting fails the build.
/// </summary>
public class LgtmRetentionTests
{
    private static string Linked(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, .. parts]);

    [Theory]
    [InlineData("CoreBankDemo.PaymentsAPI")]
    [InlineData("CoreBankDemo.CoreBankAPI")]
    public void EF_Core_command_logging_is_at_Warning(string service)
    {
        // The claim query of every poll tick was a third of all idle log volume at
        // Information; the query text is on the postgresql span of any recorded trace.
        using var settings = JsonDocument.Parse(File.ReadAllText(Linked("Services", service, "appsettings.json")));
        var level = settings.RootElement.GetProperty("Logging").GetProperty("LogLevel")
            .GetProperty("Microsoft.EntityFrameworkCore.Database.Command").GetString();

        level.Should().Be("Warning");
    }

    [Fact]
    public void Tempo_keeps_blocks_for_one_day()
    {
        // Tempo 3 has no `compactor` block; retention lives with the backend scheduler
        // (which plans the retention jobs) and the backend worker (which runs them).
        var config = File.ReadAllText(Linked("Lgtm", "tempo-config.yaml"));

        config.Should().MatchRegex(@"backend_scheduler:\s*\n\s+provider:\s*\n\s+compaction:\s*\n\s+compaction:\s*\n\s+block_retention: 24h");
        config.Should().MatchRegex(@"backend_worker:\s*\n\s+compaction:\s*\n\s+block_retention: 24h");
    }

    [Fact]
    public void Loki_deletes_chunks_older_than_one_day()
    {
        var config = File.ReadAllText(Linked("Lgtm", "loki-config.yaml"));

        config.Should().MatchRegex(@"compactor:\s*\n(?:\s+\S.*\n)*?\s+retention_enabled: true");
        config.Should().MatchRegex(@"compactor:\s*\n(?:\s+\S.*\n)*?\s+delete_request_store: filesystem");
        config.Should().MatchRegex(@"limits_config:\s*\n\s+retention_period: 24h");
    }

    [Fact]
    public void Prometheus_keeps_samples_for_one_day()
    {
        // Prometheus 3.14 deprecates --storage.tsdb.retention.time for this config field.
        var config = File.ReadAllText(Linked("Lgtm", "prometheus.yaml"));

        config.Should().MatchRegex(@"storage:\s*\n\s+tsdb:\s*\n\s+retention:\s*\n\s+time: 1d");
    }

    [Theory]
    [InlineData("CoreBankDemo.AppHost", "tempo-config.yaml")]
    [InlineData("CoreBankDemo.AppHost", "loki-config.yaml")]
    [InlineData("CoreBankDemo.AppHost", "prometheus.yaml")]
    [InlineData("CoreBankDemo.LoadTests", "tempo-config.yaml")]
    [InlineData("CoreBankDemo.LoadTests", "loki-config.yaml")]
    [InlineData("CoreBankDemo.LoadTests", "prometheus.yaml")]
    public void AppHost_mounts_the_component_config_read_only_over_the_images_file(string appHost, string file)
    {
        // ADR-022 keeps the two lgtm declarations equivalent; ADR-025 adds these mounts.
        var source = File.ReadAllText(Linked("AppHosts", appHost, "AppHost.cs"));
        var mount = new System.Text.RegularExpressions.Regex(
            @"\.WithBindMount\(\s*Path\.GetFullPath\(Path\.Combine\(builder\.AppHostDirectory, ""\.\."", ""observability"", ""lgtm"", """
            + System.Text.RegularExpressions.Regex.Escape(file)
            + @"""\)\),\s*""/otel-lgtm/" + System.Text.RegularExpressions.Regex.Escape(file) + @""",\s*isReadOnly: true\)");

        mount.IsMatch(source).Should().BeTrue($"{appHost} must mount observability/lgtm/{file} read-only at /otel-lgtm/{file}");
    }
}
