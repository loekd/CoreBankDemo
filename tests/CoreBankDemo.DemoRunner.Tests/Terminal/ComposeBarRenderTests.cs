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
/// The compose bar is asserted against the actual screen buffer, for the same reason the
/// Evidence pane is: a control whose <c>Frame.Y</c> is right can still be drawn over by the
/// line beside it, and the room reads the drawn line, not the geometry.
/// </summary>
[Collection(OperatorThemeCollection.Name)]
public class ComposeBarRenderTests
{
    /// <summary>
    /// Each mode draws its own bar under the selector, whole, at the floor and at a comfortable
    /// width; every mode is three rows, so the card's first line stays on the same screen row
    /// whichever mode is selected.
    /// </summary>
    [Theory]
    [InlineData(80, 24)]
    [InlineData(100, 30)]
    public void EachMode_DrawsItsOwnBar_AndTheCardNeverMoves(int width, int height)
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

        List<string> Draw(ComposeMode mode)
        {
            window.SelectComposeModeForTest(mode);
            window.RenderForTest();
            app.LayoutAndDraw(true);
            return AsDrawn(app, window.SubmitButton.SuperView!);
        }

        var single = Draw(ComposeMode.Single);
        var singleCardRow = window.CardStateLabel.FrameToScreen().Y;
        single[0].Should().Be(" ◉ Single  ○ Burst  ○ Fetch");
        single[1].Should().StartWith(" From").And.Contain("→ To").And.EndWith("⟦► Submit ◄⟧");
        single[2].Should().StartWith(" Amount").And.Contain("⟦ Rail ‹ standard ›⟧").And.Contain("⟦ Key ‹ Generated ›⟧");

        var burst = Draw(ComposeMode.Burst);
        burst[0].Should().Be(" ○ Single  ◉ Burst  ○ Fetch");
        burst[1].Should().StartWith(" From").And.Contain("→ To").And.EndWith("⟦► Send burst ◄⟧");
        burst[2].Should().StartWith(" Amount").And.Contain("⟦ Rail ‹ standard ›⟧")
            .And.Contain("Count").And.Contain("at once").And.NotContain("Key ‹", "a burst never reads the key mode");
        window.CardStateLabel.FrameToScreen().Y.Should().Be(singleCardRow);

        var fetch = Draw(ComposeMode.Fetch);
        fetch[0].Should().Be(" ○ Single  ○ Burst  ◉ Fetch");
        fetch[1].Should().StartWith(" Payment id").And.EndWith("⟦► Fetch ◄⟧").And.NotContain("From");
        fetch[2].Should().BeEmpty("nothing has been fetched yet");
        window.CardStateLabel.FrameToScreen().Y.Should().Be(singleCardRow);
    }

    /// <summary>
    /// The result line is drawn under the Fetch bar and stays until the next Fetch; a second,
    /// identical answer still changes the line, because its stamp is the press time.
    /// </summary>
    [Fact]
    public async Task FetchResultLine_IsDrawnUnderItsBar_AndARepeatChangesOnlyTheStamp()
    {
        using var app = TerminalAppFactory.CreateHeadless(80, 24);
        OperatorTheme.Register(ThemeMode.Dark);
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = new MainWindow(
            app, controller, () => Task.CompletedTask, null, startPolling: false, marshalUpdates: false, time: harness.Time);
        app.Begin(window);
        window.Frame = new System.Drawing.Rectangle(0, 0, 80, 24);
        window.HandleKeyForTest(Key.D1);
        window.ResizeForTest(80, 24);
        window.SelectComposeModeForTest(ComposeMode.Fetch);
        window.FetchIdField.Text = "tx-1";
        const string body =
            """{"paymentId":"tx-1","transactionId":"tx-1","status":"Pending","amount":1.00,"currency":"EUR","processedAt":"2026-08-29T11:59:58+00:00"}""";
        harness.Payments.QueueInspections(
            new InspectionResult(true, 200, KnownEndpoints.PaymentStatus, body, null, TimeSpan.FromMilliseconds(38)),
            new InspectionResult(true, 200, KnownEndpoints.PaymentStatus, body, null, TimeSpan.FromMilliseconds(41)));

        await window.TriggerFetchForTestAsync();
        window.RenderForTest();
        app.LayoutAndDraw(true);
        var first = AsDrawn(app, window.SubmitButton.SuperView!)[2];

        harness.Time.Advance(TimeSpan.FromSeconds(5));
        await window.TriggerFetchForTestAsync();
        window.RenderForTest();
        app.LayoutAndDraw(true);
        var second = AsDrawn(app, window.SubmitButton.SuperView!)[2];

        first.Should().StartWith(" ✓ 200  Pending · 1.00 EUR · since 11:59:58").And.EndWith("fetched 12:00:00 · 38 ms");
        second.Should().StartWith(" ✓ 200  Pending · 1.00 EUR · since 11:59:58").And.EndWith("fetched 12:00:05 · 41 ms");
        window.FetchStatusLabel.SchemeName.Should().Be(OperatorTheme.LockExemptScheme, "a 200 takes the teal accent");
        harness.Payments.FetchIds.Should().Equal("tx-1", "tx-1");
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
