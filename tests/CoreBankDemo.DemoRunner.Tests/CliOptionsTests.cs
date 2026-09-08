using AwesomeAssertions;
using CoreBankDemo.DemoRunner;
using CoreBankDemo.DemoRunner.Terminal;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests;

public class CliOptionsTests
{
    [Fact]
    public void Parse_NoArguments_StartsConsole()
    {
        var options = CliOptions.Parse([]);

        options.IsValid.Should().BeTrue();
        options.Doctor.Should().BeFalse();
        options.Help.Should().BeFalse();
    }

    [Theory]
    [InlineData("--doctor", true, false)]
    [InlineData("--help", false, true)]
    [InlineData("-h", false, true)]
    public void Parse_SupportedOptions_AreRecognized(string value, bool doctor, bool help)
    {
        var options = CliOptions.Parse([value]);

        options.Doctor.Should().Be(doctor);
        options.Help.Should().Be(help);
        options.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("--show")]
    [InlineData("--rehearse")]
    [InlineData("--scenario")]
    [InlineData("--resume")]
    public void Parse_RetiredCueOptions_AreRejected(string value)
    {
        var options = CliOptions.Parse([value]);

        options.IsValid.Should().BeFalse();
        options.Errors.Single().Should().Contain("retired");
    }

    [Fact]
    public void Parse_UnknownOption_IsRejected()
    {
        CliOptions.Parse(["--shell"]).Errors.Single().Should().Contain("Unknown");
        CliOptions.HelpText.Should().Contain("reusable terminal operator console");
    }

    [Theory]
    [InlineData("--light", ThemeMode.Light)]
    [InlineData("--dark", ThemeMode.Dark)]
    public void Parse_ThemeFlags_AreRecognized(string value, ThemeMode expected)
    {
        var options = CliOptions.Parse([value]);

        options.Errors.Should().BeEmpty();
        options.Theme.Should().Be(expected);
    }

    [Fact]
    public void Parse_NoThemeFlag_LeavesThemeUnset()
    {
        CliOptions.Parse([]).Theme.Should().BeNull();
    }

    [Fact]
    public void ResolveTheme_FlagWinsOverEnvironment()
    {
        CliOptions.ResolveTheme(ThemeMode.Dark, "light").Should().Be(ThemeMode.Dark);
        CliOptions.ResolveTheme(ThemeMode.Light, "dark").Should().Be(ThemeMode.Light);
    }

    [Theory]
    [InlineData("light", ThemeMode.Light)]
    [InlineData("LIGHT", ThemeMode.Light)]
    [InlineData("  light  ", ThemeMode.Light)]
    [InlineData("dark", ThemeMode.Dark)]
    public void ResolveTheme_ReadsEnvironmentWhenNoFlagGiven(string value, ThemeMode expected)
    {
        CliOptions.ResolveTheme(null, value).Should().Be(expected);
    }

    /// <summary>
    /// A stale or misspelled profile variable must never stop the console starting, least of
    /// all minutes before a talk, so an unrecognized value falls back rather than erroring.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("solarized")]
    public void ResolveTheme_DefaultsToDark(string? value)
    {
        CliOptions.ResolveTheme(null, value).Should().Be(ThemeMode.Dark);
    }

    [Fact]
    public void HelpText_DocumentsThemeSelection()
    {
        CliOptions.HelpText.Should().Contain("--light");
        CliOptions.HelpText.Should().Contain(CliOptions.ThemeEnvironmentVariable);
    }
}
