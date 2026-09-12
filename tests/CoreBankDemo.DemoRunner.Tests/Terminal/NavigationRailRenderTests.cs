using System.Text;
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Terminal;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

/// <summary>
/// The rail is asserted against the actual screen buffer rather than against view geometry,
/// because geometry is what let an empty rail ship. A Button reserves a one-cell right/bottom
/// Margin for its drop shadow; pinning the row to a single line left <c>Frame.Height</c> at 1
/// and <c>Viewport.Height</c> at 0, so every row drew nothing while <c>Frame</c>,
/// <c>Visible</c> and <c>Text</c> all still read exactly as a passing test expected. Only the
/// rendered cells tell the truth about a terminal UI.
/// </summary>
[Collection(OperatorThemeCollection.Name)]
public class NavigationRailRenderTests
{
    private const int RailInnerWidth = 14;

    [Fact]
    public void EveryWorkspaceRendersOnItsOwnRow_LeftAlignedAndUnwrapped()
    {
        var rows = RenderRail();

        rows.Should().ContainInOrder(
            "▸1 Operations",
            "2 Resources",
            "3 Evidence",
            "4 Tests",
            "5 Faults");
    }

    /// <summary>
    /// The shortcut digits form a column the operator aims at mid-sentence, so every row starts
    /// at the same cell. An undecorated Button centres its caption by default, which put the
    /// five rows at five different indents.
    /// </summary>
    [Fact]
    public void EveryRowStartsAtTheSameColumn()
    {
        var rows = RenderRail();
        var indents = rows
            .Where(row => row.TrimStart().Length > 0)
            .Select(row => row.Length - row.TrimStart().Length)
            .Distinct();

        indents.Should().ContainSingle("a ragged rail has no column of shortcuts to aim at");
    }

    /// <summary>Reads the rail's cells straight out of the driver's output buffer.</summary>
    private static List<string> RenderRail()
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
        var rows = new List<string>();
        // Rail rows start below the window border, the topology bar and the rail's own
        // border; column 2 is the first cell inside the rail.
        for (var row = 3; row < 3 + 10; row++)
        {
            var line = new StringBuilder();
            for (var column = 2; column < 2 + RailInnerWidth; column++)
            {
                var grapheme = contents[row, column].Grapheme;
                line.Append(string.IsNullOrEmpty(grapheme) || grapheme == "\0" ? " " : grapheme);
            }

            var text = line.ToString().TrimEnd();
            if (text.Trim().Length > 0)
            {
                rows.Add(text.Trim());
            }
        }

        return rows;
    }
}
