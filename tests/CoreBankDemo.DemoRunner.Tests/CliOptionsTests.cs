using AwesomeAssertions;
using CoreBankDemo.DemoRunner;
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
}
