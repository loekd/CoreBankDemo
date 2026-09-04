using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Terminal;
using Terminal.Gui.Input;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

public class LinkDialogTests
{
    private const string Escape = "\u001b";
    private const string Bell = "\u0007";

    [Fact]
    public void Construction_ShowsTheUrlReadOnlyAndPreSelected()
    {
        using var dialog = new LinkDialog(
            "Jaeger", "http://localhost:16686", new StringWriter(), termEnvironment: null, tmuxEnvironment: null);

        dialog.Title.Should().Be("Jaeger");
        dialog.UrlFieldText.Should().Be("http://localhost:16686");
        dialog.UrlFieldIsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void CopyLink_WritesTheUrlToTheTerminalClipboardAndReportsSuccessInline()
    {
        var terminal = new StringWriter();
        using var dialog = new LinkDialog(
            "Aspire dashboard", "http://127.0.0.1:15888", terminal, termEnvironment: null, tmuxEnvironment: null);

        dialog.CopyButton.InvokeCommand(Command.Accept);

        terminal.ToString().Should().Be($"{Escape}]52;c;aHR0cDovLzEyNy4wLjAuMToxNTg4OA=={Bell}");
        dialog.StatusText.Should().Contain("terminal clipboard");
    }

    [Fact]
    public void CloseButton_IsTheDefaultAcceptView()
    {
        using var dialog = new LinkDialog(
            "Jaeger", "http://localhost:16686", new StringWriter(), termEnvironment: null, tmuxEnvironment: null);

        dialog.DefaultAcceptView.Should().BeSameAs(dialog.CloseButton);
    }
}
