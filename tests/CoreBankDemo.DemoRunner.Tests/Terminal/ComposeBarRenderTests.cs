using System.Text;
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Terminal;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

/// <summary>
/// The compose bar is asserted against the actual screen buffer, for the same reason the
/// Evidence pane is: a control whose <c>Frame.Y</c> is right can still be drawn over by the
/// line beside it, and the room reads the drawn line, not the geometry.
/// </summary>
[Collection(OperatorThemeCollection.Name)]
public class ComposeBarRenderTests
{
    /// <summary>
    /// The mode button draws on its own third line at both the 80x24 floor and a comfortable
    /// width, so the chips' line is drawn whole, and arming a burst puts the count fields on
    /// that same line beside the button rather than on a line of their own.
    /// </summary>
    [Theory]
    [InlineData(80, 24)]
    [InlineData(100, 30)]
    public void TheModeButton_DrawsOnItsOwnLine_AndTheBurstFieldsShareIt(int width, int height)
    {
        using var app = TerminalAppFactory.CreateHeadless(width, height);
        OperatorTheme.Register(ThemeMode.Dark);
        var controller = new OperatorHarness().CreateController();
        using var window = new MainWindow(
            app, controller, () => Task.CompletedTask, null, startPolling: false, marshalUpdates: false);
        app.Begin(window);
        window.Frame = new System.Drawing.Rectangle(0, 0, width, height);
        window.HandleKeyForTest(Key.D1);
        window.ResizeForTest(width, height);
        window.RenderForTest();
        app.LayoutAndDraw(true);

        var rows = AsDrawn(app, window.SubmitButton.SuperView!);
        rows[0].Should().StartWith(" From").And.Contain("→ To").And.EndWith("⟦► Submit ◄⟧");
        // Each chip is drawn whole, closing chevron and bracket included: a chip two cells
        // narrower than its decorated caption reflowed its "›" onto the line beneath.
        rows[1].Should().StartWith(" Amount").And.Contain("⟦ Rail ‹ standard ›⟧").And.Contain("⟦ Key ‹ Generated ›⟧");
        rows[2].TrimStart().Should().Be("⟦ Burst mode ⟧", "nothing else draws on the mode line in single mode");

        window.BurstButton.InvokeCommand(Command.Accept);
        window.RenderForTest();
        app.LayoutAndDraw(true);

        rows = AsDrawn(app, window.SubmitButton.SuperView!);
        rows[0].Should().EndWith("⟦► Send burst ◄⟧");
        rows[1].Should().Contain("⟦ Rail ‹ standard ›⟧").And.Contain("⟦ Key ‹ Generated ›⟧");
        rows[2].Should().StartWith(" Burst count").And.Contain("at once").And.EndWith("⟦ Single mode ⟧");
    }

    private static List<string> AsDrawn(IApplication app, View pane)
    {
        var origin = pane.FrameToScreen();
        var contents = app.Driver!.Contents!;
        var lines = new List<string>();
        for (var row = 0; row < pane.Frame.Height; row++)
        {
            var line = new StringBuilder();
            for (var offset = 0; offset < pane.Frame.Width; offset++)
            {
                var grapheme = contents[origin.Y + row, origin.X + offset].Grapheme;
                line.Append(string.IsNullOrEmpty(grapheme) || grapheme == "\0" ? " " : grapheme);
            }

            lines.Add(line.ToString().TrimEnd());
        }

        return lines;
    }
}
