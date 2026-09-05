using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

/// <summary>
/// Guards the checked-in AppHost launch profiles against the failure that made
/// the Aspire dashboard unreachable whenever DemoRunner started the topology.
/// <para>
/// The Aspire CLI injects the selected profile's <c>applicationUrl</c> as the
/// <c>ASPNETCORE_URLS</c> <em>environment variable</em>, which outranks the
/// <c>ASPNETCORE_URLS</c> key in the AppHost's <c>appsettings.json</c>. The
/// tool-generated profiles this repository used to carry pointed at random
/// HTTPS ports (<c>https://localhost:17253</c> and friends), so the dashboard
/// silently moved off the documented <c>http://localhost:15888</c> that
/// <c>.devcontainer/devcontainer.json</c> forwards and README/ARCHITECTURE
/// advertise. That is invisible when the CLI prints the real URL into an
/// interactive terminal and fatal when it does not: DemoRunner starts the
/// AppHost detached (<c>aspire start</c>), so nothing echoes the URL and
/// nothing forwards the port.
/// </para>
/// <para>
/// Regenerating launch settings (opening the solution in Visual Studio, for
/// instance) reintroduces exactly that shape, so this asserts the invariant
/// rather than trusting the file to stay put: one HTTP-only profile per
/// AppHost, no HTTPS or random ports, and the regular AppHost's dashboard
/// endpoint agreeing with its own <c>appsettings.json</c>.
/// </para>
/// </summary>
public class AppHostLaunchProfileTests
{
    [Theory]
    [InlineData("CoreBankDemo.AppHost")]
    [InlineData("CoreBankDemo.LoadTests")]
    public void Launch_profiles_are_http_only_so_the_dashboard_endpoint_is_deterministic(string appHostProject)
    {
        var profiles = ReadProfiles(appHostProject);

        profiles.EnumerateObject().Should().NotBeEmpty($"{appHostProject} must declare a launch profile");
        foreach (var profile in profiles.EnumerateObject())
        {
            var applicationUrl = profile.Value.GetProperty("applicationUrl").GetString();
            applicationUrl.Should().NotBeNullOrWhiteSpace();
            applicationUrl.Should().NotContain(
                "https://",
                $"profile '{profile.Name}' would move {appHostProject}'s dashboard onto an HTTPS port that no "
                + "container/sandbox port forward and no browser dev-cert trust is set up for");

            var environment = profile.Value.GetProperty("environmentVariables");
            foreach (var name in new[] { "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL" })
            {
                environment.GetProperty(name).GetString()
                    .Should().StartWith("http://", $"profile '{profile.Name}' must keep {name} on plain HTTP");
            }
        }
    }

    /// <summary>
    /// The dashboard must also bind every interface, not just loopback. Aspire/DCP
    /// binds its own managed endpoints to 127.0.0.1, but the dashboard reads
    /// ASPNETCORE_URLS directly, so this one is ours to set. A loopback-only
    /// dashboard is unreachable from a browser outside the container that runs it
    /// -- exactly the case this repository's own .devcontainer and sandbox setups
    /// are in -- and no host-side port publish can rescue it.
    /// </summary>
    [Fact]
    public void Regular_apphost_dashboard_url_matches_its_appsettings_and_the_documented_port()
    {
        var profiles = ReadProfiles("CoreBankDemo.AppHost");
        var configured = JsonDocument
            .Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), "CoreBankDemo.AppHost", "appsettings.json")))
            .RootElement.GetProperty("ASPNETCORE_URLS").GetString();

        configured.Should().Be("http://0.0.0.0:15888");
        foreach (var profile in profiles.EnumerateObject())
        {
            profile.Value.GetProperty("applicationUrl").GetString()
                .Should().Be(
                    configured,
                    "an environment-variable applicationUrl overrides appsettings.json, so the two must agree or "
                    + "the dashboard silently moves off the port the docs and devcontainer forward");
        }
    }

    private static JsonElement ReadProfiles(string appHostProject)
    {
        var path = Path.Combine(FindRepoRoot(), appHostProject, "Properties", "launchSettings.json");
        File.Exists(path).Should().BeTrue($"{path} is checked in and governs the dashboard endpoint");

        // Kept alive for the caller: JsonDocument owns the memory behind its elements,
        // so clone out of the disposable document rather than handing back a dangling view.
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("profiles").Clone();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CoreBankDemo.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"Could not locate repo root (CoreBankDemo.sln) walking up from {AppContext.BaseDirectory}");
        }

        return directory.FullName;
    }
}
