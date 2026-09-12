using System.Text;
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Terminal;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

/// <summary>
/// The Details pane is asserted against the actual screen buffer rather than against
/// <c>TextView.Text</c>, for the same reason the navigation rail is: a pane whose scroll offset
/// is one column out holds exactly the right text and draws the wrong thing. Every geometry and
/// text assertion in <c>MainWindowTests</c> passed while the RESPONSE column was rendering as
/// <c>ESPONSE</c> on a real terminal.
/// </summary>
[Collection(OperatorThemeCollection.Name)]
public class EvidencePaneRenderTests
{
    /// <summary>
    /// Moving the caret in a column, reading another record and coming back must not leave the
    /// pane scrolled. <c>TextView</c> keeps its viewport offset across an assignment to
    /// <c>Text</c>, so the caret left behind by an arrow key or a click dragged the next record
    /// one column left: every line lost its first character and a line holding nothing but
    /// <c>{</c> disappeared entirely.
    /// </summary>
    [Fact]
    public async Task ReadingAnotherRecordAndComingBack_DrawsTheResponseColumnFromItsFirstCharacter()
    {
        using var app = TerminalAppFactory.CreateHeadless(140, 40);
        OperatorTheme.Register(ThemeMode.Dark);
        using var window = await EvidenceWindowAsync(app);
        app.Begin(window);
        window.Frame = new System.Drawing.Rectangle(0, 0, 140, 40);
        window.HandleKeyForTest(Key.D3);
        window.ResizeForTest(140, 40);
        window.RenderForTest();
        app.LayoutAndDraw(true);

        // The operator reads into the column, then walks the list and comes back.
        window.EvidenceResponsePane.SetFocus();
        window.EvidenceResponsePane.NewKeyDownEvent(Key.End);
        window.EvidenceList.SelectedItem = 1;
        window.RenderForTest();
        app.LayoutAndDraw(true);
        window.EvidenceList.SelectedItem = 0;
        window.RenderForTest();
        app.LayoutAndDraw(true);

        window.EvidenceResponsePane.Viewport.Location.X.Should().Be(
            0, "a new record is read from its first column, not from the last one's offset");

        var column = ResponseColumnAsDrawn(app, window);
        column[0].Should().Be("RESPONSE");
        column.Should().Contain("HTTP 202 Accepted");
        column.Should().Contain("{", "a line holding only an opening brace is the first casualty of a one-column scroll");
    }

    /// <summary>
    /// The payload draws on the workspace's own surface, at the workspace's own text colour.
    /// A <see cref="Scheme"/> built from a single <see cref="Attribute"/> lets Terminal.Gui
    /// blend the roles it was not given, and a <c>ReadOnly</c> <c>TextView</c> picks the blend:
    /// the pane rendered <c>#C9D3DE</c> on <c>#536B88</c> — about 3.4:1 — while every geometry,
    /// text and border assertion passed. The border carries the separation; the fill does not.
    /// </summary>
    [Theory]
    [InlineData(ThemeMode.Dark, "#E8ECF1", "#0B1220")]
    [InlineData(ThemeMode.Light, "#1F2328", "#FFFFFF")]
    public async Task ThePayloadDrawsOnTheWorkspacesOwnSurface(ThemeMode mode, string foreground, string background)
    {
        using var app = TerminalAppFactory.CreateHeadless(140, 40);
        // The window registers the palette itself, so it is the one that must be told.
        using var window = await EvidenceWindowAsync(app, mode);
        app.Begin(window);
        window.Frame = new System.Drawing.Rectangle(0, 0, 140, 40);
        window.HandleKeyForTest(Key.D3);
        window.ResizeForTest(140, 40);
        window.RenderForTest();
        app.LayoutAndDraw(true);

        var origin = window.EvidenceResponsePane.FrameToScreen();
        var cell = app.Driver!.Contents![origin.Y, origin.X];

        cell.Grapheme.Should().Be("R", "this is the first cell of the RESPONSE column");
        cell.Attribute!.Value.Foreground.Should().Be(new Color(foreground));
        cell.Attribute!.Value.Background.Should().Be(
            new Color(background),
            "the pane is the same surface as the rest of the workspace, not a dimmed variant");
    }

    /// <summary>A console holding one payment record that carries a full HTTP exchange.</summary>
    private static async Task<MainWindow> EvidenceWindowAsync(IApplication app, ThemeMode theme = ThemeMode.Dark)
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "payment-id", "tx-8821", "Pending",
            Body, null, TimeSpan.FromMilliseconds(5),
            new HttpExchange(
                "POST",
                "http://127.0.0.1:5294/api/payments",
                [new EvidenceHeader("Idempotency-Key", "demo-key-001")],
                Body,
                202,
                "Accepted",
                [new EvidenceHeader("Content-Type", "application/json")],
                Body)));
        await controller.SubmitPaymentAsync(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Generated, null, CancellationToken.None);
        return new MainWindow(
            app, controller, () => Task.CompletedTask, null, startPolling: false, marshalUpdates: false, theme: theme);
    }

    private const string Body =
        """{"transactionId":"tx-8821","status":"Pending","note":"long enough that the column can scroll"}""";

    /// <summary>Reads the RESPONSE column's cells straight off the driver's screen buffer.</summary>
    private static List<string> ResponseColumnAsDrawn(IApplication app, MainWindow window)
    {
        var pane = window.EvidenceResponsePane;
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
