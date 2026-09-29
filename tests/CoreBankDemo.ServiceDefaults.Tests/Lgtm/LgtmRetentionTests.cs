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
}
