using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Infrastructure;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

public class TerminalClipboardTests
{
    private const string Escape = "\u001b";
    private const string Bell = "\u0007";

    // "aGVsbG8=" is base64 for "hello"; the terminal emulator sets its own
    // clipboard from this payload, which is what makes the copy work with no
    // display server and no clipboard helper binary.
    private const string HelloSequence = $"{Escape}]52;c;aGVsbG8={Bell}";

    [Fact]
    public void Copy_EmitsAnOsc52SequenceCarryingTheBase64Payload()
    {
        var terminal = new StringWriter();

        var result = TerminalClipboard.Copy("hello", terminal);

        result.Succeeded.Should().BeTrue();
        terminal.ToString().Should().Be(HelloSequence);
        result.Message.Should().Contain("5 characters");
    }

    [Fact]
    public void Copy_TunnelsThroughTmuxPassthroughWithDoubledEscapes()
    {
        var terminal = new StringWriter();

        TerminalClipboard.Copy(
            "hello",
            terminal,
            termEnvironment: "screen-256color",
            tmuxEnvironment: "/tmp/tmux-1000/default,123,0");

        terminal.ToString().Should()
            .Be($"{Escape}Ptmux;{Escape}{Escape}]52;c;aGVsbG8={Bell}{Escape}\\");
    }

    [Fact]
    public void Copy_TunnelsThroughScreenPassthroughWhenTmuxIsAbsent()
    {
        var terminal = new StringWriter();

        TerminalClipboard.Copy("hello", terminal, termEnvironment: "screen.xterm-256color");

        terminal.ToString().Should().Be($"{Escape}P{HelloSequence}{Escape}\\");
    }

    [Fact]
    public void Copy_RefusesAnOversizedPayloadRatherThanWriteACorruptTruncation()
    {
        var terminal = new StringWriter();
        var oversized = new string('x', TerminalClipboard.MaxBase64Length);

        var result = TerminalClipboard.Copy(oversized, terminal);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Export session evidence");
        terminal.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Copy_ReportsAnEmptySelectionInsteadOfWritingAnEmptyClipboard()
    {
        var terminal = new StringWriter();

        var result = TerminalClipboard.Copy(string.Empty, terminal);

        result.Succeeded.Should().BeFalse();
        terminal.ToString().Should().BeEmpty();
    }
}
