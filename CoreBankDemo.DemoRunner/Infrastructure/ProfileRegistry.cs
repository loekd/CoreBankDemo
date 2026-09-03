using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Infrastructure;

public static class ProfileRegistry
{
    public static string ProjectPath(string repositoryRoot, TopologyProfile profile) =>
        Path.Combine(repositoryRoot, RelativeProjectPath(profile).Replace('/', Path.DirectorySeparatorChar));

    public static string RelativeProjectPath(TopologyProfile profile) => profile switch
    {
        TopologyProfile.Regular => "CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj",
        TopologyProfile.LoadTests => "CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };
}
