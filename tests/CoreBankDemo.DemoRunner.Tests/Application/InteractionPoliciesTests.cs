using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application;

public class InteractionPoliciesTests
{
    [Theory]
    [InlineData('Y', true)]
    [InlineData('y', false)]
    [InlineData('\r', false)]
    [InlineData('N', false)]
    public void Confirmation_OnlyUppercaseYConfirms(char key, bool expected)
    {
        InteractionPolicies.ConfirmsDestructiveAction(key).Should().Be(expected);
    }

    [Theory]
    [InlineData(100, 30, TerminalLayoutMode.Preferred)]
    [InlineData(80, 24, TerminalLayoutMode.Compact)]
    [InlineData(79, 24, TerminalLayoutMode.BelowMinimum)]
    [InlineData(80, 23, TerminalLayoutMode.BelowMinimum)]
    public void Layout_UsesNormativeTerminalThresholds(int width, int height, TerminalLayoutMode expected)
    {
        InteractionPolicies.LayoutFor(width, height).Should().Be(expected);
    }
}
