using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Infrastructure;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

/// <summary>
/// Exercises the real <see cref="CommandRunner"/>, not a fake. The whole arming chain rests
/// on the child process actually receiving <c>Features__UseDevProxy</c>, and a fake that
/// records a dictionary proves nothing about <see cref="System.Diagnostics.ProcessStartInfo"/>.
/// </summary>
public class CommandRunnerTests
{
    [Fact]
    public async Task RunAsync_AppliesTheSuppliedEnvironmentOnTopOfTheInheritedOne()
    {
        RequirePrintenv();

        var result = await new CommandRunner().RunAsync(
            "printenv",
            [],
            Path.GetTempPath(),
            TimeSpan.FromSeconds(20),
            CancellationToken.None,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Features__UseDevProxy"] = "true",
            });

        result.Succeeded.Should().BeTrue(result.StandardError);
        var variables = Parse(result.StandardOutput);
        variables.Should().ContainKey("Features__UseDevProxy").WhoseValue.Should().Be("true");
        // Layered, never replaced: the child still needs the inherited environment to find and
        // run the Aspire CLI at all.
        variables.Should().ContainKey("PATH");
        variables["PATH"].Should().Be(Environment.GetEnvironmentVariable("PATH"));
    }

    [Fact]
    public async Task RunAsync_WithoutAnEnvironment_LeavesTheInheritedOneUntouched()
    {
        RequirePrintenv();

        var result = await new CommandRunner().RunAsync(
            "printenv",
            [],
            Path.GetTempPath(),
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.StandardError);
        var variables = Parse(result.StandardOutput);
        variables.Should().NotContainKey("Features__UseDevProxy");
        variables.Should().ContainKey("PATH");
    }

    [Fact]
    public async Task RunAsync_MissingExecutable_IsReportedRatherThanThrown()
    {
        var result = await new CommandRunner().RunAsync(
            $"corebank-not-a-real-binary-{Guid.NewGuid():N}",
            [],
            Path.GetTempPath(),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.ProcessStarted.Should().BeFalse();
        result.StartFailed.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
    }

    private static void RequirePrintenv()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("'printenv' is a POSIX utility; this asserts the POSIX process launch path.");
        }
    }

    private static Dictionary<string, string> Parse(string output)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                variables[line[..separator]] = line[(separator + 1)..].TrimEnd('\r');
            }
        }

        return variables;
    }
}
