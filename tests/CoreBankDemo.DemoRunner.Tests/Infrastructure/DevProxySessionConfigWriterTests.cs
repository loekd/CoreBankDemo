using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Infrastructure;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

public class DevProxySessionConfigWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"corebank-devproxy-{Guid.NewGuid():N}");

    public DevProxySessionConfigWriterTests()
    {
        Directory.CreateDirectory(ProfileRegistry.DevProxyConfigDirectory(_root, TopologyProfile.Regular));
        Directory.CreateDirectory(ProfileRegistry.DevProxyConfigDirectory(_root, TopologyProfile.LoadTests));
        File.WriteAllText(ProfileRegistry.CheckedInConfigPath(_root, TopologyProfile.Regular), RegularProfile);
        File.WriteAllText(ProfileRegistry.CheckedInErrorsPath(_root, TopologyProfile.Regular), ErrorsProfile);
        File.WriteAllText(ProfileRegistry.CheckedInConfigPath(_root, TopologyProfile.LoadTests), LatencyProfile);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Write_LandsOnlyInTheGeneratedSiblingDirectoryAndNeverTouchesACheckedInFile()
    {
        var writer = new DevProxySessionConfigWriter(_root);
        var before = CheckedInSnapshot();

        var result = await writer.WriteAsync(
            TopologyProfile.Regular,
            new FaultLevels(40, 800, 2000, 100),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Path.Should().Be(ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.Regular));
        File.Exists(result.Path).Should().BeTrue();
        CheckedInSnapshot().Should().Equal(before, "the checked-in profiles are read-only preset sources");
    }

    [Fact]
    public async Task Write_CommitsEveryKnobInOneConfigSeededFromTheCheckedInProfile()
    {
        var writer = new DevProxySessionConfigWriter(_root);

        await writer.WriteAsync(
            TopologyProfile.Regular,
            new FaultLevels(40, 800, 2000, 100),
            CancellationToken.None);

        using var document = JsonDocument.Parse(
            File.ReadAllText(ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.Regular)));
        var root = document.RootElement;
        root.GetProperty("errorsCoreBank").GetProperty("rate").GetInt32().Should().Be(40);
        root.GetProperty("latency").GetProperty("minMs").GetInt32().Should().Be(800);
        root.GetProperty("latency").GetProperty("maxMs").GetInt32().Should().Be(2000);
        root.GetProperty("rateLimiting").GetProperty("rateLimit").GetInt32().Should().Be(100);

        // Inherited from the seed rather than reinvented.
        root.GetProperty("port").GetInt32().Should().Be(8000);
        root.GetProperty("urlsToWatch")[0].GetString().Should().Be("http://127.0.0.1:5032/api/*");
        root.GetProperty("plugins").EnumerateArray()
            .Select(plugin => plugin.GetProperty("pluginPath").GetString())
            .Should().AllBe("~appFolder/plugins/DevProxy.Plugins.dll");
    }

    [Fact]
    public async Task Write_KeepsErrorsFileARelativeSiblingNameAndWritesThatSibling()
    {
        var writer = new DevProxySessionConfigWriter(_root);

        await writer.WriteAsync(TopologyProfile.Regular, new FaultLevels(25, 0, 0, 0), CancellationToken.None);

        using var document = JsonDocument.Parse(
            File.ReadAllText(ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.Regular)));
        document.RootElement.GetProperty("errorsCoreBank").GetProperty("errorsFile").GetString()
            .Should().Be("devproxy-errors.session.json");
        var sibling = ProfileRegistry.GeneratedErrorsPath(_root, TopologyProfile.Regular);
        File.Exists(sibling).Should().BeTrue();
        Path.GetDirectoryName(sibling).Should().Be(
            Path.GetDirectoryName(ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.Regular)));
        File.ReadAllText(sibling).Should().Contain("Service temporarily unavailable");
    }

    [Fact]
    public async Task Write_AZeroKnobDisablesItsPluginRatherThanDeletingIt()
    {
        var writer = new DevProxySessionConfigWriter(_root);

        // Latency only: the other two knobs are zero and must be present but disabled.
        await writer.WriteAsync(TopologyProfile.Regular, new FaultLevels(0, 800, 2000, 0), CancellationToken.None);

        using var document = JsonDocument.Parse(
            File.ReadAllText(ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.Regular)));
        var plugins = document.RootElement.GetProperty("plugins").EnumerateArray().ToList();
        plugins.Should().HaveCount(3, "the file always describes all three knobs so a later read is unambiguous");
        Enabled(plugins, "LatencyPlugin").Should().BeTrue();
        Enabled(plugins, "RateLimitingPlugin").Should().BeFalse();
        Enabled(plugins, "GenericRandomErrorPlugin").Should().BeFalse();
        (await writer.ReadAsync(TopologyProfile.Regular, CancellationToken.None)).Levels
            .Should().Be(new FaultLevels(0, 800, 2000, 0));
    }

    [Fact]
    public async Task Write_AllZero_KeepsOneInertPluginEnabledSoDevProxyCanStartAtAll()
    {
        var writer = new DevProxySessionConfigWriter(_root);

        await writer.WriteAsync(TopologyProfile.Regular, FaultLevels.AllZero, CancellationToken.None);

        using var document = JsonDocument.Parse(
            File.ReadAllText(ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.Regular)));
        var root = document.RootElement;
        var plugins = root.GetProperty("plugins").EnumerateArray().ToList();

        // Dev Proxy throws "No plugins configured or enabled" and exits when every plugin is
        // disabled, which would make panic-off kill the proxy outright.
        plugins.Should().HaveCount(3);
        Enabled(plugins, "LatencyPlugin").Should().BeTrue("one plugin must stay enabled or the proxy will not start");
        Enabled(plugins, "RateLimitingPlugin").Should().BeFalse();
        Enabled(plugins, "GenericRandomErrorPlugin").Should().BeFalse();

        // Enabled never means injecting: the keep-alive plugin's own config is a no-op band.
        root.GetProperty("latency").GetProperty("minMs").GetInt32().Should().Be(0);
        root.GetProperty("latency").GetProperty("maxMs").GetInt32().Should().Be(0);

        // And the readback is unchanged, so the chip still reads Armed rather than in force.
        var read = await writer.ReadAsync(TopologyProfile.Regular, CancellationToken.None);
        read.Levels.Should().Be(FaultLevels.AllZero);
        read.Levels.IsAllZero.Should().BeTrue();
        read.Levels.MatchingPresetName(TopologyProfile.Regular).Should().Be("All off");
    }

    [Fact]
    public async Task Reset_AlsoLeavesAStartablePlugin_SoArmingCannotBringUpAProxyThatExits()
    {
        var writer = new DevProxySessionConfigWriter(_root);

        await writer.ResetAsync(TopologyProfile.Regular, CancellationToken.None);

        using var document = JsonDocument.Parse(
            File.ReadAllText(ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.Regular)));
        document.RootElement.GetProperty("plugins").EnumerateArray()
            .Should().Contain(plugin => plugin.GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(40, 0, 0, 0)]
    [InlineData(0, 800, 2000, 0)]
    [InlineData(0, 0, 0, 100)]
    [InlineData(40, 800, 2000, 0)]
    [InlineData(40, 0, 0, 100)]
    [InlineData(0, 800, 2000, 100)]
    [InlineData(40, 800, 2000, 100)]
    public async Task EveryKnobCombination_LeavesAStartableConfigAndRoundTripsExactly(
        int errorRate,
        int floor,
        int ceiling,
        int throttle)
    {
        var levels = new FaultLevels(errorRate, floor, ceiling, throttle);
        var writer = new DevProxySessionConfigWriter(_root);

        await writer.WriteAsync(TopologyProfile.Regular, levels, CancellationToken.None);

        using var document = JsonDocument.Parse(
            File.ReadAllText(ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.Regular)));
        var plugins = document.RootElement.GetProperty("plugins").EnumerateArray().ToList();
        plugins.Should().Contain(
            plugin => plugin.GetProperty("enabled").GetBoolean(),
            "Dev Proxy refuses to start with no enabled plugin, for every knob combination");
        (await writer.ReadAsync(TopologyProfile.Regular, CancellationToken.None)).Levels.Should().Be(levels);
    }

    [Theory]
    [InlineData(TopologyProfile.Regular)]
    [InlineData(TopologyProfile.LoadTests)]
    public async Task AllZero_LeavesNoPluginConfiguredToInjectAnything(TopologyProfile profile)
    {
        var writer = new DevProxySessionConfigWriter(_root);

        await writer.WriteAsync(profile, FaultLevels.AllZero, CancellationToken.None);

        using var document = JsonDocument.Parse(
            File.ReadAllText(ProfileRegistry.GeneratedConfigPath(_root, profile)));
        var root = document.RootElement;
        root.GetProperty("latency").GetProperty("minMs").GetInt32().Should().Be(0);
        root.GetProperty("latency").GetProperty("maxMs").GetInt32().Should().Be(0);
        root.GetProperty("rateLimiting").GetProperty("rateLimit").GetInt32().Should().Be(0);
        root.GetProperty("errorsCoreBank").GetProperty("rate").GetInt32().Should().Be(0);
        (await writer.ReadAsync(profile, CancellationToken.None)).Levels.IsAllZero.Should().BeTrue();
    }

    private static bool Enabled(IEnumerable<JsonElement> plugins, string name) =>
        plugins.Single(plugin => plugin.GetProperty("name").GetString() == name)
            .GetProperty("enabled").GetBoolean();

    [Fact]
    public async Task Write_AddsAPluginTheSeedProfileDoesNotDeclare()
    {
        var writer = new DevProxySessionConfigWriter(_root);

        await writer.WriteAsync(TopologyProfile.LoadTests, new FaultLevels(25, 9500, 12000, 0), CancellationToken.None);

        using var document = JsonDocument.Parse(
            File.ReadAllText(ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.LoadTests)));
        var plugins = document.RootElement.GetProperty("plugins").EnumerateArray().ToList();
        var errorPlugin = plugins.Should()
            .ContainSingle(plugin => plugin.GetProperty("name").GetString() == "GenericRandomErrorPlugin").Subject;
        errorPlugin.GetProperty("enabled").GetBoolean().Should().BeTrue();
        errorPlugin.GetProperty("urlsToWatch")[0].GetString().Should().Be("http://127.0.0.1:5032/api/Transactions/*");
        // The LoadTests profile ships no errors file; a default is synthesized beside the config.
        File.ReadAllText(ProfileRegistry.GeneratedErrorsPath(_root, TopologyProfile.LoadTests))
            .Should().Contain("http://127.0.0.1:5032/api/Transactions/*");
    }

    [Fact]
    public async Task Read_WithoutAGeneratedConfig_ReportsTheCheckedInProfile()
    {
        var writer = new DevProxySessionConfigWriter(_root);

        var read = await writer.ReadAsync(TopologyProfile.Regular, CancellationToken.None);

        read.Succeeded.Should().BeTrue();
        read.FromGeneratedSession.Should().BeFalse();
        read.Levels.Should().Be(FaultLevels.CheckedInDefaults(TopologyProfile.Regular));
    }

    [Fact]
    public async Task Read_PrefersTheGeneratedSessionConfigAndRoundTripsEveryKnob()
    {
        var writer = new DevProxySessionConfigWriter(_root);
        var levels = new FaultLevels(40, 800, 2000, 100);
        await writer.WriteAsync(TopologyProfile.Regular, levels, CancellationToken.None);

        var read = await writer.ReadAsync(TopologyProfile.Regular, CancellationToken.None);

        read.Succeeded.Should().BeTrue();
        read.FromGeneratedSession.Should().BeTrue();
        read.Levels.Should().Be(levels);
    }

    [Fact]
    public async Task Read_AfterAnAllZeroWrite_ReportsQuietRatherThanTheSeedLevels()
    {
        var writer = new DevProxySessionConfigWriter(_root);
        await writer.ResetAsync(TopologyProfile.Regular, CancellationToken.None);

        var read = await writer.ReadAsync(TopologyProfile.Regular, CancellationToken.None);

        read.Levels.Should().Be(FaultLevels.AllZero);
        read.FromGeneratedSession.Should().BeTrue();
    }

    [Fact]
    public async Task Read_MalformedGeneratedConfig_FallsBackToTheCheckedInDefaultsAndNamesTheFailedRead()
    {
        var writer = new DevProxySessionConfigWriter(_root);
        Directory.CreateDirectory(ProfileRegistry.GeneratedConfigDirectory(_root, TopologyProfile.Regular));
        File.WriteAllText(ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.Regular), "{ not json");

        var read = await writer.ReadAsync(TopologyProfile.Regular, CancellationToken.None);

        read.Succeeded.Should().BeFalse();
        read.Levels.Should().Be(FaultLevels.CheckedInDefaults(TopologyProfile.Regular));
        read.ErrorSummary.Should().Contain("devproxyrc.session.json");
    }

    [Fact]
    public async Task Read_MissingCheckedInProfile_StillReportsTheProfileDefaultsNeverAnInventedZero()
    {
        File.Delete(ProfileRegistry.CheckedInConfigPath(_root, TopologyProfile.LoadTests));
        var writer = new DevProxySessionConfigWriter(_root);

        var read = await writer.ReadAsync(TopologyProfile.LoadTests, CancellationToken.None);

        read.Succeeded.Should().BeFalse();
        read.Levels.Should().Be(FaultLevels.CheckedInDefaults(TopologyProfile.LoadTests));
        read.ErrorSummary.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Write_WithNoSeedProfile_FailsWithoutCreatingAnything()
    {
        File.Delete(ProfileRegistry.CheckedInConfigPath(_root, TopologyProfile.LoadTests));
        var writer = new DevProxySessionConfigWriter(_root);

        var result = await writer.WriteAsync(TopologyProfile.LoadTests, FaultLevels.AllZero, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorSummary.Should().NotBeNullOrWhiteSpace();
        File.Exists(ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.LoadTests)).Should().BeFalse();
    }

    [Fact]
    public async Task NoneProfile_HasNothingToReadOrWrite()
    {
        var writer = new DevProxySessionConfigWriter(_root);

        (await writer.ReadAsync(TopologyProfile.None, CancellationToken.None)).Levels
            .Should().Be(FaultLevels.AllZero);
        (await writer.WriteAsync(TopologyProfile.None, FaultLevels.AllZero, CancellationToken.None)).Succeeded
            .Should().BeFalse();
    }

    [Fact]
    public async Task Write_MalformedSeedProfile_IsReportedRatherThanThrown()
    {
        File.WriteAllText(ProfileRegistry.CheckedInConfigPath(_root, TopologyProfile.Regular), "[]");
        var writer = new DevProxySessionConfigWriter(_root);

        var result = await writer.WriteAsync(TopologyProfile.Regular, FaultLevels.AllZero, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorSummary.Should().Contain("not a Dev Proxy configuration object");
    }

    [Fact]
    public void GeneratedPaths_SitBesideTheCheckedInProfileTheyBelongTo()
    {
        var generated = ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.Regular);
        var checkedIn = ProfileRegistry.CheckedInConfigPath(_root, TopologyProfile.Regular);

        Path.GetDirectoryName(generated).Should().Be(
            Path.Combine(Path.GetDirectoryName(checkedIn)!, "generated"));
        ProfileRegistry.CheckedInConfigFileName(TopologyProfile.LoadTests).Should().Be("devproxyrc-latency.json");
    }

    private string[] CheckedInSnapshot() =>
        [
            .. Directory
                .EnumerateFiles(ProfileRegistry.DevProxyConfigDirectory(_root, TopologyProfile.Regular))
                .Concat(Directory.EnumerateFiles(ProfileRegistry.DevProxyConfigDirectory(_root, TopologyProfile.LoadTests)))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => $"{path}:{File.ReadAllText(path)}"),
        ];

    [Fact]
    public async Task Write_PreservesSeedPropertiesThisConsoleDoesNotModel()
    {
        File.WriteAllText(
            ProfileRegistry.CheckedInConfigPath(_root, TopologyProfile.Regular),
            RegularProfile.Replace(
                "\"rateLimiting\": { \"costPerRequest\": 1, \"rateLimit\": 1000, \"resetTimeWindowSeconds\": 60 }",
                "\"rateLimiting\": { \"costPerRequest\": 7, \"rateLimit\": 1000, \"resetTimeWindowSeconds\": 120, \"headerLimit\": \"x-rate\" }",
                StringComparison.Ordinal));
        var writer = new DevProxySessionConfigWriter(_root);

        await writer.WriteAsync(TopologyProfile.Regular, new FaultLevels(0, 0, 0, 250), CancellationToken.None);

        using var document = JsonDocument.Parse(
            File.ReadAllText(ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.Regular)));
        var rateLimiting = document.RootElement.GetProperty("rateLimiting");
        rateLimiting.GetProperty("rateLimit").GetInt32().Should().Be(250, "the knob is written");
        rateLimiting.GetProperty("costPerRequest").GetInt32().Should().Be(7, "the seed's value is not overwritten");
        rateLimiting.GetProperty("resetTimeWindowSeconds").GetInt32().Should().Be(120);
        rateLimiting.GetProperty("headerLimit").GetString().Should().Be("x-rate", "a property we do not model survives");
    }

    [Fact]
    public async Task Write_DisablesEveryDeclarationOfAPluginTheSeedNamesTwice()
    {
        File.WriteAllText(
            ProfileRegistry.CheckedInConfigPath(_root, TopologyProfile.LoadTests),
            LatencyProfile.Replace(
                "\"plugins\": [",
                "\"plugins\": [ { \"name\": \"LatencyPlugin\", \"enabled\": true, \"configSection\": \"latency\", \"urlsToWatch\": [\"http://127.0.0.1:5032/api/Accounts/*\"] },",
                StringComparison.Ordinal));
        var writer = new DevProxySessionConfigWriter(_root);

        // Errors on, latency at zero: something else is enabled, so the keep-alive plugin
        // rule does not apply and every latency declaration must really go quiet.
        await writer.WriteAsync(TopologyProfile.LoadTests, new FaultLevels(40, 0, 0, 0), CancellationToken.None);

        using var document = JsonDocument.Parse(
            File.ReadAllText(ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.LoadTests)));
        var latencyPlugins = document.RootElement.GetProperty("plugins").EnumerateArray()
            .Where(plugin => plugin.GetProperty("name").GetString() == "LatencyPlugin")
            .ToList();
        latencyPlugins.Should().HaveCount(2);
        latencyPlugins.Should().OnlyContain(plugin => plugin.GetProperty("enabled").GetBoolean() == false,
            "one declaration left enabled would keep injecting after the knob reached zero");
        (await writer.ReadAsync(TopologyProfile.LoadTests, CancellationToken.None)).Levels
            .Should().Be(new FaultLevels(40, 0, 0, 0));
    }

    [Fact]
    public async Task AllZero_WithADuplicatedKeepAlivePlugin_StillReadsBackAsQuiet()
    {
        File.WriteAllText(
            ProfileRegistry.CheckedInConfigPath(_root, TopologyProfile.LoadTests),
            LatencyProfile.Replace(
                "\"plugins\": [",
                "\"plugins\": [ { \"name\": \"LatencyPlugin\", \"enabled\": true, \"configSection\": \"latency\", \"urlsToWatch\": [\"http://127.0.0.1:5032/api/Accounts/*\"] },",
                StringComparison.Ordinal));
        var writer = new DevProxySessionConfigWriter(_root);

        await writer.WriteAsync(TopologyProfile.LoadTests, FaultLevels.AllZero, CancellationToken.None);

        using var document = JsonDocument.Parse(
            File.ReadAllText(ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.LoadTests)));
        var root = document.RootElement;
        // Both declarations are re-enabled as the keep-alive, and both read the same 0/0
        // section, so "enabled" still does not mean "injecting".
        root.GetProperty("plugins").EnumerateArray()
            .Where(plugin => plugin.GetProperty("name").GetString() == "LatencyPlugin")
            .Should().OnlyContain(plugin => plugin.GetProperty("enabled").GetBoolean());
        root.GetProperty("latency").GetProperty("maxMs").GetInt32().Should().Be(0);
        (await writer.ReadAsync(TopologyProfile.LoadTests, CancellationToken.None)).Levels
            .Should().Be(FaultLevels.AllZero);
    }

    [Theory]
    // A seed profile is a hand-editable file outside this console's control, so a wrong-typed
    // node must degrade to "not present" rather than escape as an unhandled exception.
    [InlineData("\"name\": \"LatencyPlugin\"", "\"name\": 42")]
    [InlineData("\"configSection\": \"latency\"", "\"configSection\": []")]
    [InlineData("\"enabled\": true", "\"enabled\": \"yes\"")]
    [InlineData("\"urlsToWatch\": [\"http://127.0.0.1:5032/api/*\"]", "\"urlsToWatch\": [17]")]
    [InlineData("\"port\": 8000,", "\"port\": 8000, \"port\": 8001,")]
    public async Task WrongTypedOrDuplicatedSeedNodes_AreReported_NeverThrown(string original, string replacement)
    {
        File.WriteAllText(
            ProfileRegistry.CheckedInConfigPath(_root, TopologyProfile.Regular),
            RegularProfile.Replace(original, replacement, StringComparison.Ordinal));
        var writer = new DevProxySessionConfigWriter(_root);

        var write = await writer.WriteAsync(TopologyProfile.Regular, new FaultLevels(5, 20, 200, 1000), CancellationToken.None);
        var read = await writer.ReadAsync(TopologyProfile.Regular, CancellationToken.None);

        // Either outcome is acceptable; escaping as an exception is not.
        if (!write.Succeeded)
        {
            write.ErrorSummary.Should().NotBeNullOrWhiteSpace();
        }

        read.Levels.Should().NotBeNull();
    }

    [Fact]
    public async Task Write_LeavesNoTemporaryFileBehind()
    {
        var writer = new DevProxySessionConfigWriter(_root);

        await writer.WriteAsync(TopologyProfile.Regular, new FaultLevels(5, 20, 200, 1000), CancellationToken.None);

        // Dev Proxy watches the final path, so the content lands in a temp file first; none of
        // those may survive, or the watched directory fills with partial documents.
        Directory.EnumerateFiles(ProfileRegistry.GeneratedConfigDirectory(_root, TopologyProfile.Regular))
            .Should().OnlyContain(path => !path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Write_ErrorsFileCoversTheSameUrlSurfaceTheOtherPluginsWatch()
    {
        var writer = new DevProxySessionConfigWriter(_root);

        await writer.WriteAsync(TopologyProfile.Regular, new FaultLevels(40, 0, 0, 0), CancellationToken.None);

        using var errors = JsonDocument.Parse(
            File.ReadAllText(ProfileRegistry.GeneratedErrorsPath(_root, TopologyProfile.Regular)));
        var entries = errors.RootElement.GetProperty("errors").EnumerateArray().ToList();
        entries.Should().ContainSingle();
        var request = entries[0].GetProperty("request");
        request.GetProperty("url").GetString().Should().Be(
            "http://127.0.0.1:5032/api/*",
            "scoping injection to POST /api/accounts/validate would make the error knob unobservable");
        request.TryGetProperty("method", out _).Should().BeFalse("every verb the surface serves is eligible");
        // The shipped response bodies are still the preset source.
        entries[0].GetProperty("responses")[0].GetProperty("body").GetProperty("error").GetString()
            .Should().Be("Service temporarily unavailable");
    }

    [Fact]
    public async Task Delete_RemovesBothGeneratedFilesAndTheDirectory()
    {
        var writer = new DevProxySessionConfigWriter(_root);
        await writer.WriteAsync(TopologyProfile.Regular, new FaultLevels(5, 20, 200, 1000), CancellationToken.None);

        var deleted = await writer.DeleteAsync(TopologyProfile.Regular, CancellationToken.None);

        deleted.Succeeded.Should().BeTrue();
        File.Exists(ProfileRegistry.GeneratedConfigPath(_root, TopologyProfile.Regular)).Should().BeFalse();
        File.Exists(ProfileRegistry.GeneratedErrorsPath(_root, TopologyProfile.Regular)).Should().BeFalse();
        Directory.Exists(ProfileRegistry.GeneratedConfigDirectory(_root, TopologyProfile.Regular)).Should().BeFalse();
        // With the session config gone the AppHost falls back to the checked-in profile again.
        (await writer.ReadAsync(TopologyProfile.Regular, CancellationToken.None)).FromGeneratedSession
            .Should().BeFalse();
    }

    [Fact]
    public async Task Delete_IsIdempotentAndSafeWithNothingToRemove()
    {
        var writer = new DevProxySessionConfigWriter(_root);

        (await writer.DeleteAsync(TopologyProfile.Regular, CancellationToken.None)).Succeeded.Should().BeTrue();
        (await writer.DeleteAsync(TopologyProfile.None, CancellationToken.None)).Succeeded.Should().BeTrue();
    }

    private const string RegularProfile = """
        {
          "plugins": [
            { "name": "RateLimitingPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "rateLimiting", "urlsToWatch": ["http://127.0.0.1:5032/api/*"] },
            { "name": "LatencyPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "latency", "urlsToWatch": ["http://127.0.0.1:5032/api/*"] },
            { "name": "GenericRandomErrorPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "errorsCoreBank", "urlsToWatch": ["http://127.0.0.1:5032/api/*"] }
          ],
          "port": 8000,
          "record": false,
          "errorsCoreBank": { "errorsFile": "devproxy-errors.json", "rate": 5 },
          "rateLimiting": { "costPerRequest": 1, "rateLimit": 1000, "resetTimeWindowSeconds": 60 },
          "latency": { "minMs": 20, "maxMs": 200 },
          "urlsToWatch": ["http://127.0.0.1:5032/api/*"]
        }
        """;

    private const string LatencyProfile = """
        {
          "plugins": [
            { "name": "LatencyPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "latency", "urlsToWatch": ["http://127.0.0.1:5032/api/Transactions/*"] }
          ],
          "port": 8001,
          "record": false,
          "latency": { "minMs": 9500, "maxMs": 12000 },
          "urlsToWatch": ["http://127.0.0.1:5032/api/Transactions/*"]
        }
        """;

    private const string ErrorsProfile = """
        {
          "errors": [
            {
              "request": { "url": "http://127.0.0.1:5032/api/accounts/validate", "method": "POST" },
              "responses": [ { "statusCode": 503, "body": { "error": "Service temporarily unavailable" } } ]
            }
          ]
        }
        """;
}
