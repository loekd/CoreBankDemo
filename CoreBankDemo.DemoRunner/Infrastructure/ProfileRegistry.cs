using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Infrastructure;

public static class ProfileRegistry
{
    /// <summary>
    /// Name of the generated session config both AppHosts prefer over their checked-in
    /// profile when it exists. It is a sibling of the checked-in file on purpose:
    /// <c>errorsFile</c> resolves relative to the rc file, so a repo-root dot-directory
    /// would break it.
    /// </summary>
    public const string GeneratedConfigDirectoryName = "generated";
    public const string GeneratedConfigFileName = "devproxyrc.session.json";
    public const string GeneratedErrorsFileName = "devproxy-errors.session.json";

    public static string ProjectPath(string repositoryRoot, TopologyProfile profile) =>
        Path.Combine(repositoryRoot, RelativeProjectPath(profile).Replace('/', Path.DirectorySeparatorChar));

    public static string RelativeProjectPath(TopologyProfile profile) => profile switch
    {
        TopologyProfile.Regular => "CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj",
        TopologyProfile.LoadTests => "CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    public static string AppHostDirectory(string repositoryRoot, TopologyProfile profile) =>
        Path.GetDirectoryName(ProjectPath(repositoryRoot, profile))
        ?? throw new ArgumentOutOfRangeException(nameof(profile));

    public static string DevProxyConfigDirectory(string repositoryRoot, TopologyProfile profile) =>
        Path.Combine(AppHostDirectory(repositoryRoot, profile), "devproxy", "config");

    /// <summary>Read-only preset source. This console never writes here.</summary>
    public static string CheckedInConfigPath(string repositoryRoot, TopologyProfile profile) =>
        Path.Combine(DevProxyConfigDirectory(repositoryRoot, profile), CheckedInConfigFileName(profile));

    public static string CheckedInConfigFileName(TopologyProfile profile) => profile switch
    {
        TopologyProfile.Regular => "devproxyrc.json",
        TopologyProfile.LoadTests => "devproxyrc-latency.json",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    /// <summary>Read-only preset source. This console never writes here.</summary>
    public static string CheckedInErrorsPath(string repositoryRoot, TopologyProfile profile) =>
        Path.Combine(DevProxyConfigDirectory(repositoryRoot, profile), "devproxy-errors.json");

    public static string GeneratedConfigDirectory(string repositoryRoot, TopologyProfile profile) =>
        Path.Combine(DevProxyConfigDirectory(repositoryRoot, profile), GeneratedConfigDirectoryName);

    public static string GeneratedConfigPath(string repositoryRoot, TopologyProfile profile) =>
        Path.Combine(GeneratedConfigDirectory(repositoryRoot, profile), GeneratedConfigFileName);

    public static string GeneratedErrorsPath(string repositoryRoot, TopologyProfile profile) =>
        Path.Combine(GeneratedConfigDirectory(repositoryRoot, profile), GeneratedErrorsFileName);
}
