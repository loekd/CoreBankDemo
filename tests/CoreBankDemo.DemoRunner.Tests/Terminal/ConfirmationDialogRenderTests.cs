using System.Text;
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Terminal;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

/// <summary>
/// The destructive confirmation modal draws an OK button beside Cancel and a hint that names
/// both ways to confirm. Asserted against the screen buffer, for the reason
/// <see cref="NavigationRailRenderTests"/> gives.
/// </summary>
[Collection(OperatorThemeCollection.Name)]
public class ConfirmationDialogRenderTests
{
    [Fact]
    public void DialogDrawsOkLeftOfCancelOnOneRow()
    {
        var rows = RenderDialog();

        var buttonRow = rows.Should().ContainSingle(row => row.Contains("Cancel")).Subject;
        buttonRow.Should().Contain("OK");
        buttonRow.IndexOf("OK", StringComparison.Ordinal)
            .Should().BeLessThan(buttonRow.IndexOf("Cancel", StringComparison.Ordinal));
    }

    [Fact]
    public void DialogDrawsTheWholeConfirmationHint()
    {
        var rows = RenderDialog();

        rows.Should().Contain(row =>
            row.Contains("Press uppercase Y or choose OK to confirm. Enter/Escape cancel."));
    }

    /// <summary>Reads every drawn row straight out of the driver's output buffer.</summary>
    private static IReadOnlyList<string> RenderDialog()
    {
        using var app = TerminalAppFactory.CreateHeadless(100, 30);
        OperatorTheme.Register(ThemeMode.Dark);
        using var dialog = new DestructiveConfirmationDialog(
            new ConfirmationRequest("Restart", "aspire resource x restart", ["x"]));
        app.Begin(dialog);
        app.LayoutAndDraw(true);

        var contents = app.Driver!.Contents!;
        var rows = new List<string>();
        for (var row = 0; row < 30; row++)
        {
            var line = new StringBuilder();
            for (var column = 0; column < 100; column++)
            {
                var grapheme = contents[row, column].Grapheme;
                line.Append(string.IsNullOrEmpty(grapheme) || grapheme == "\0" ? " " : grapheme);
            }

            rows.Add(line.ToString().TrimEnd());
        }

        return rows;
    }
}
