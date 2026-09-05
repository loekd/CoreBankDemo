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
