using System.Text;
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Terminal;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

/// <summary>
/// The Load Test results are asserted against the actual screen buffer: the room reads the
/// drawn rows, and a failed run must point at the invariant that failed.
/// </summary>
[Collection(OperatorThemeCollection.Name)]
public class LoadResultsRenderTests
{
    /// <summary>
    /// ADR-026: a misrouted payment fails the run, and the per-account ordering row draws as
    /// the failure, with the check's own detail -- not a failed run beside five green rows.
    /// </summary>
    [Fact]
    public async Task AFailedRoutingCheck_DrawsThePerAccountOrderingRowAsFailed()
    {
        using var app = TerminalAppFactory.CreateHeadless(140, 40);
        OperatorTheme.Register(ThemeMode.Dark);
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.LoadTests));
        harness.Load.Result = LoadWorkflowResult.Failure(
            LoadWorkflowPhase.Assert,
            "LoadTestSupport reported allPassed=false.",
            [
                new InvariantResult("Exactly-once processing", true, "unique"),
                new InvariantResult("Zero message loss", true, "all"),
                new InvariantResult("Balance conservation", true, "balanced"),
                new InvariantResult("Terminal-state completeness", true, "drained"),
                new InvariantResult("Per-account ordering", false, "ordered | accounts ordered | 1 misrouted row"),
            ],
            "details",
            new InlineSettlementResult(true, "observed"));
        var controller = harness.CreateController();
        (await controller.AttachAsync(TopologyProfile.LoadTests, CancellationToken.None)).Succeeded.Should().BeTrue();
        using var window = new MainWindow(
            app, controller, () => Task.CompletedTask, null, startPolling: false, marshalUpdates: false);
        app.Begin(window);
        window.Frame = new System.Drawing.Rectangle(0, 0, 140, 40);
        window.HandleKeyForTest(Key.D4);

        await window.TriggerLoadForTestAsync(100);
        window.ResizeForTest(140, 40);
        window.RenderForTest();
        app.LayoutAndDraw(true);

        var rows = AsDrawn(app, window.LoadResultsList).Where(line => line.Length > 0).ToList();
        rows.Should().Contain("✕ FAIL Per-account ordering: ordered | accounts ordered | 1 misrouted row");
        rows.Should().NotContain(row => row.Contains("Per-key ordering"));
        rows.Where(row => row.StartsWith("✕ FAIL", StringComparison.Ordinal)).Should().ContainSingle();
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
