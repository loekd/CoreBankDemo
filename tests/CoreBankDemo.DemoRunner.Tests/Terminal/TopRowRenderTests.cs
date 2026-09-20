using System.Text;
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Terminal;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

/// <summary>
/// The shell's top row carries the Aspire and LGTM quick-open buttons and nothing else; the
/// topology line that used to share it is gone. Asserted against the screen buffer, for the
/// reason <see cref="NavigationRailRenderTests"/> gives.
/// </summary>
[Collection(OperatorThemeCollection.Name)]
public class TopRowRenderTests
{
    [Fact]
    public void TopRowShowsOnlyTheAspireAndLgtmButtons()
    {
        var row = RenderTopRow();

        row.Trim().Should().Be("⟦ Aspire ⟧ ⟦ LGTM ⟧", "nothing else is drawn on the top row");
    }

    /// <summary>Reads the row under the window border straight out of the driver's output buffer.</summary>
    private static string RenderTopRow()
    {
        using var app = TerminalAppFactory.CreateHeadless(100, 30);
        OperatorTheme.Register(ThemeMode.Dark);
        var controller = new OperatorHarness().CreateController();
        using var window = new MainWindow(
            app, controller, () => Task.CompletedTask, null, startPolling: false, marshalUpdates: false);
        app.Begin(window);
        window.Frame = new System.Drawing.Rectangle(0, 0, 100, 30);
        app.LayoutAndDraw(true);

        var contents = app.Driver!.Contents!;
        var line = new StringBuilder();
        for (var column = 1; column < 99; column++)
        {
            var grapheme = contents[1, column].Grapheme;
            line.Append(string.IsNullOrEmpty(grapheme) || grapheme == "\0" ? " " : grapheme);
        }

        return line.ToString().TrimEnd();
    }
}
