using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Infrastructure;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

/// <summary>
/// Ties the console's compiled-in knowledge to the files it actually claims to describe.
/// Restating the same literals in the same assembly proves nothing: these read the real
/// checked-in profiles and the real AppHost sources.
/// </summary>
public class CheckedInDevProxyProfileTests
{
    [Theory]
    [InlineData(TopologyProfile.Regular)]
    [InlineData(TopologyProfile.LoadTests)]
    public async Task CheckedInDefaults_MatchTheProfileTheyClaimToDescribe(TopologyProfile profile)
    {
        var root = RepositoryRoot();
        var source = ProfileRegistry.CheckedInConfigPath(root, profile);
        File.Exists(source).Should().BeTrue($"{source} is the read-only preset source for {profile}");

        // Copied into a scratch root so the assertion is about the real file's contents and
        // not about whether a live console session happens to hold a generated config right
        // now. Read through the adapter so the real parse path is what is exercised.
        var scratch = Path.Combine(Path.GetTempPath(), $"corebank-checkedin-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(ProfileRegistry.DevProxyConfigDirectory(scratch, profile));
            File.Copy(source, ProfileRegistry.CheckedInConfigPath(scratch, profile));

            var read = await new DevProxySessionConfigWriter(scratch).ReadAsync(profile, CancellationToken.None);

            read.Succeeded.Should().BeTrue();
            read.FromGeneratedSession.Should().BeFalse();
            read.Levels.Should().Be(
                FaultLevels.CheckedInDefaults(profile),
                "the preset chips promise the levels the profile really ships");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Theory]
    [InlineData(TopologyProfile.Regular, "CoreBankDemo.AppHost/AppHost.cs")]
    [InlineData(TopologyProfile.LoadTests, "CoreBankDemo.LoadTests/AppHost.cs")]
    public void GeneratedConfigPath_IsThePathTheAppHostActuallyProbes(TopologyProfile profile, string appHostSource)
    {
        var root = RepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, appHostSource.Replace('/', Path.DirectorySeparatorChar)));
        var generated = ProfileRegistry.GeneratedConfigPath(root, profile);

        // The AppHost builds its path from AppHostDirectory, so the two agree only if they
        // use the same segments. Assert on the segments rather than on a whole path string.
        source.Should().Contain($"\"{ProfileRegistry.GeneratedConfigDirectoryName}\"");
        source.Should().Contain($"\"{ProfileRegistry.GeneratedConfigFileName}\"");
        source.Should().Contain($"\"{ProfileRegistry.CheckedInConfigFileName(profile)}\"");
        source.Should().Contain("File.Exists(generatedConfigFile)");

        generated.Should().EndWith(Path.Combine(
            "devproxy",
            "config",
            ProfileRegistry.GeneratedConfigDirectoryName,
            ProfileRegistry.GeneratedConfigFileName));
        Path.GetDirectoryName(Path.GetDirectoryName(generated))
            .Should().Be(ProfileRegistry.DevProxyConfigDirectory(root, profile));
    }

    [Fact]
    public void CheckedInProfilesAndTheirErrorsFileAreNeverGeneratedPaths()
    {
        var root = RepositoryRoot();
        foreach (var profile in KnownTopologyProfiles.All)
        {
            var generatedDirectory = ProfileRegistry.GeneratedConfigDirectory(root, profile);
            ProfileRegistry.CheckedInConfigPath(root, profile).Should().NotStartWith(generatedDirectory);
            ProfileRegistry.CheckedInErrorsPath(root, profile).Should().NotStartWith(generatedDirectory);
        }
    }

    [Theory]
    [InlineData(ErrorMix.Mixed)]
    [InlineData(ErrorMix.Retryable)]
    public async Task EveryRegularMix_HasACheckedInErrorsFileTheConsoleWritesAndRecognises(ErrorMix mix)
    {
        var root = RepositoryRoot();
        var source = ProfileRegistry.CheckedInErrorsPath(root, TopologyProfile.Regular, mix);
        File.Exists(source).Should().BeTrue($"{source} is the errors file the {mix} mix is copied from (ADR-031)");

        var scratch = Path.Combine(Path.GetTempPath(), $"corebank-mix-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(ProfileRegistry.DevProxyConfigDirectory(scratch, TopologyProfile.Regular));
            File.Copy(
                ProfileRegistry.CheckedInConfigPath(root, TopologyProfile.Regular),
                ProfileRegistry.CheckedInConfigPath(scratch, TopologyProfile.Regular));
            foreach (var candidate in Enum.GetValues<ErrorMix>())
            {
                File.Copy(
                    ProfileRegistry.CheckedInErrorsPath(root, TopologyProfile.Regular, candidate),
                    ProfileRegistry.CheckedInErrorsPath(scratch, TopologyProfile.Regular, candidate));
            }

            var writer = new DevProxySessionConfigWriter(scratch);
            var levels = new FaultLevels(40, 100, 400, 0, mix);
            (await writer.WriteAsync(TopologyProfile.Regular, levels, CancellationToken.None)).Succeeded.Should().BeTrue();
            var read = await writer.ReadAsync(TopologyProfile.Regular, CancellationToken.None);

            read.Levels.Should().Be(levels, "the two real files differ, so a session written from one must read back as that one");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void TheRegularErrorsFiles_FaultWhatTheirMixPromises()
    {
        var root = RepositoryRoot();
        using var mixed = JsonDocument.Parse(File.ReadAllText(
            ProfileRegistry.CheckedInErrorsPath(root, TopologyProfile.Regular, ErrorMix.Mixed)));
        using var retryable = JsonDocument.Parse(File.ReadAllText(
            ProfileRegistry.CheckedInErrorsPath(root, TopologyProfile.Regular, ErrorMix.Retryable)));

        var mixedByUrl = EntriesByUrl(mixed);
        var retryableByUrl = EntriesByUrl(retryable);
        const string process = "http://127.0.0.1:5032/api/transactions/process";
        const string cancel = "http://127.0.0.1:5032/api/transactions/cancel";

        // Retryable: 409/423 on the transfer command only. Nothing the breaker counts, no cancel.
        retryableByUrl.Keys.Should().Equal(process);
        retryableByUrl[process].Should().OnlyContain(status => status == 409 || status == 423);

        // Mixed: retried 409/423, a rare 500, and a breaker-blind 423 on the cancel. No 400: a
        // proxy-made verdict is a ghost CoreBank never recorded, so the dashboards cannot count it.
        mixedByUrl.Keys.Should().Equal(process, cancel);
        var picks = mixedByUrl[process];
        picks.Count(status => status == 409 || status == 423).Should().Be(18, "18 of 19 picks are retried without the breaker noticing");
        picks.Should().NotContain(400, "an injected 400 ends the row Failed at PaymentsAPI while CoreBank recorded nothing, so the dashboards and the console disagree (ADR-031)");
        picks.Count(status => status == 500).Should().Be(1, "1 of 19 picks is the only answer the breaker counts");
        picks.Should().HaveCount(19);
        mixedByUrl[cancel].Should().Equal(new[] { 423 }, "a faulted cancel must not count for the breaker either");
    }

    [Fact]
    public void TheRegularProfile_ErrorPluginWatchesTheCancelSoTheMixedFileCanReachIt()
    {
        var root = RepositoryRoot();
        using var rc = JsonDocument.Parse(File.ReadAllText(ProfileRegistry.CheckedInConfigPath(root, TopologyProfile.Regular)));

        var plugins = rc.RootElement.GetProperty("plugins").EnumerateArray()
            .ToDictionary(
                plugin => plugin.GetProperty("name").GetString()!,
                plugin => plugin.GetProperty("urlsToWatch").EnumerateArray().Select(url => url.GetString()).ToList());

        plugins["GenericRandomErrorPlugin"].Should().Equal(
            ["http://127.0.0.1:5032/api/transactions/process", "http://127.0.0.1:5032/api/transactions/cancel"],
            "only the error plugin reaches the cancel, and only the mixed errors file names it");
        plugins["LatencyPlugin"].Should().Equal(
            ["http://127.0.0.1:5032/api/transactions/process"],
            "latency on the cancel would overrun the cancel allowance and turn every exhausted payment into a 202 (ADR-030)");
        plugins["RateLimitingPlugin"].Should().Equal(["http://127.0.0.1:5032/api/transactions/process"]);
    }

    private static Dictionary<string, List<int>> EntriesByUrl(JsonDocument errors) =>
        errors.RootElement.GetProperty("errors").EnumerateArray().ToDictionary(
            entry => entry.GetProperty("request").GetProperty("url").GetString()!,
            entry => entry.GetProperty("responses").EnumerateArray()
                .Select(response => response.GetProperty("statusCode").GetInt32())
                .ToList());

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CoreBankDemo.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests run from inside the repository");
        return directory!.FullName;
    }
}
