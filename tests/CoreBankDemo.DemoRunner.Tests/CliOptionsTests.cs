using AwesomeAssertions;
using CoreBankDemo.DemoRunner;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests;

public class CliOptionsTests
{
    [Fact]
    public void Parse_NoArgs_DefaultsToShowFalseAndDefaultScenario()
    {
        var options = CliOptions.Parse([]);

        options.Doctor.Should().BeFalse();
        options.Show.Should().BeFalse();
        options.Rehearse.Should().BeFalse();
        options.Resume.Should().BeFalse();
        options.ScenarioName.Should().Be(CliOptions.DefaultScenarioName);
    }

    [Theory]
    [InlineData("--doctor")]
    [InlineData("--show")]
    [InlineData("--rehearse")]
    [InlineData("--resume")]
    public void Parse_EachFlag_IsRecognizedIndependently(string flag)
    {
        var options = CliOptions.Parse([flag]);

        (options.Doctor || options.Show || options.Rehearse || options.Resume).Should().BeTrue();
    }

    [Fact]
    public void Parse_ScenarioFlag_OverridesDefaultName()
    {
        var options = CliOptions.Parse(["--scenario", "my-other-talk"]);

        options.ScenarioName.Should().Be("my-other-talk");
    }

    [Fact]
    public void Parse_CombinedFlags_AllApply()
    {
        var options = CliOptions.Parse(["--rehearse", "--scenario", "talk-x", "--resume"]);

        options.Rehearse.Should().BeTrue();
        options.Resume.Should().BeTrue();
        options.ScenarioName.Should().Be("talk-x");
    }

    [Fact]
    public void Parse_ScenarioFlagWithoutValue_IsIgnoredSafely()
    {
        var act = () => CliOptions.Parse(["--scenario"]);

        act.Should().NotThrow();
    }
}
