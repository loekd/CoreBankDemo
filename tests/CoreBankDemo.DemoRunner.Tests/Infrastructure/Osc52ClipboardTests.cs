using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Infrastructure;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

public class Osc52ClipboardTests
{
    private const string Escape = "";
    private const string Bell = "";

    [Fact]
    public void TrySetClipboardData_WritesOsc52ToTheTerminalAndReportsSuccess()
    {
        var terminal = new StringWriter();
        var clipboard = new Osc52Clipboard(terminal, termEnvironment: null, tmuxEnvironment: null);
        ClipboardCopyResult? reported = null;
        clipboard.Copied += result => reported = result;

        var ok = clipboard.TrySetClipboardData("hello");

        ok.Should().BeTrue();
        terminal.ToString().Should().Be($"{Escape}]52;c;aGVsbG8={Bell}");
        reported.Should().NotBeNull();
        reported!.Succeeded.Should().BeTrue();
        reported.Message.Should().Contain("terminal clipboard");
    }

    [Fact]
    public void TryGetClipboardData_ReturnsWhatWasLastCopiedFromTheConsole()
    {
        // Reading the emulator's clipboard back needs a round trip that most
        // terminals prompt for or refuse, so paste inside the console hands
        // back the console's own last copy rather than nothing at all.
        var clipboard = new Osc52Clipboard(new StringWriter(), null, null);
        clipboard.TrySetClipboardData("first");
        clipboard.TrySetClipboardData("second");

        var ok = clipboard.TryGetClipboardData(out var contents);

        ok.Should().BeTrue();
        contents.Should().Be("second");
    }

    [Fact]
    public void TrySetClipboardData_WithAnEmptySelection_ReportsFalseWithoutWritingOrThrowing()
    {
        // Terminal.Gui swallows only NotSupportedException on this path;
        // anything else would escape through a Ctrl+C key press and take the
        // console down with it, so a failed copy must surface as false.
        var terminal = new StringWriter();
        var clipboard = new Osc52Clipboard(terminal, null, null);
        ClipboardCopyResult? reported = null;
        clipboard.Copied += result => reported = result;

        var ok = clipboard.TrySetClipboardData(string.Empty);

        ok.Should().BeFalse();
        terminal.ToString().Should().BeEmpty();
        reported.Should().NotBeNull();
        reported!.Succeeded.Should().BeFalse();
    }

    [Fact]
    public void TrySetClipboardData_WithAnOversizedPayload_ReportsFalseAndKeepsThePreviousCopy()
    {
        var terminal = new StringWriter();
        var clipboard = new Osc52Clipboard(terminal, null, null);
        clipboard.TrySetClipboardData("kept");
        terminal.GetStringBuilder().Clear();

        var ok = clipboard.TrySetClipboardData(new string('x', TerminalClipboard.MaxBase64Length));

        ok.Should().BeFalse();
        terminal.ToString().Should().BeEmpty();
        clipboard.TryGetClipboardData(out var contents).Should().BeTrue();
        contents.Should().Be("kept");
    }

    [Fact]
    public void TrySetClipboardData_UnderTmux_UsesThePassthroughWrapper()
    {
        var terminal = new StringWriter();
        var clipboard = new Osc52Clipboard(terminal, "screen-256color", "/tmp/tmux-1000/default,1,0");

        clipboard.TrySetClipboardData("hello");

        terminal.ToString().Should().StartWith($"{Escape}Ptmux;").And.EndWith($"{Escape}\\");
    }

    [Fact]
    public void IsSupported_IsAlwaysTrue_BecauseNoHelperBinaryOrDisplayIsInvolved()
    {
        new Osc52Clipboard(new StringWriter(), null, null).IsSupported.Should().BeTrue();
    }
}
