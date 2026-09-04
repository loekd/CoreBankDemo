using CoreBankDemo.DemoRunner.Infrastructure;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using AppTerminal = Terminal.Gui.App.Application;

namespace CoreBankDemo.DemoRunner.Terminal;

/// <summary>
/// Shows a known link the OS could not open directly, so the operator can
/// still reach it: the URL sits in a read-only, pre-selected field (a modern
/// terminal's own URL detection makes it clickable, and a plain drag-select
/// works regardless), and Copy link puts it on the terminal clipboard via
/// OSC 52 -- the same mechanism <see cref="MainWindow"/>'s evidence pane uses,
/// since the OS clipboard has the identical no-display-server problem here.
/// </summary>
internal interface ILinkFallbackPresenter
{
    void Show(string title, string url);
}

internal sealed class TerminalLinkFallbackPresenter(TextWriter terminal, string? termEnvironment, string? tmuxEnvironment)
    : ILinkFallbackPresenter
{
    public void Show(string title, string url) =>
        AppTerminal.Run(new LinkDialog(title, url, terminal, termEnvironment, tmuxEnvironment));
}

#pragma warning disable CS0618
internal sealed class LinkDialog : Dialog
{
    private readonly string _url;
    private readonly TextWriter _terminal;
    private readonly string? _termEnvironment;
    private readonly string? _tmuxEnvironment;
    private readonly TextField _urlField;
    private readonly Label _status;

    internal LinkDialog(string title, string url, TextWriter terminal, string? termEnvironment, string? tmuxEnvironment)
    {
        _url = url;
        _terminal = terminal;
        _termEnvironment = termEnvironment;
        _tmuxEnvironment = tmuxEnvironment;
        Title = title;
        Width = Math.Max(50, Math.Min(96, url.Length + 6));
        Height = 8;
        OperatorTheme.Apply(this, OperatorTheme.OverlayScheme);

        Add(new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = 1,
            Text = "Could not launch a browser from here. Select the link or press Copy link.",
        });

        _urlField = new TextField
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Height = 1,
            Text = url,
            ReadOnly = true,
        };
        Add(_urlField);

        _status = new Label { X = 1, Y = 3, Width = Dim.Fill(1), Height = 1, Text = string.Empty };
        Add(_status);

        CopyButton = new Button { Text = "Copy link", ShadowStyle = ShadowStyles.None };
        CopyButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            CopyLink();
        };

        CloseButton = new Button { Text = "Close", IsDefault = true, ShadowStyle = ShadowStyles.None };
        CloseButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            RequestStop();
        };

        AddButton(CopyButton);
        AddButton(CloseButton);
        DefaultAcceptView = CloseButton;
        _urlField.SetFocus();
        _urlField.SelectAll();
    }

    internal Button CopyButton { get; }
    internal Button CloseButton { get; }
    internal string StatusText => _status.Text;
    internal string UrlFieldText => _urlField.Text;
    internal bool UrlFieldIsReadOnly => _urlField.ReadOnly;

    internal void CopyLink()
    {
        var result = TerminalClipboard.Copy(_url, _terminal, _termEnvironment, _tmuxEnvironment);
        _status.Text = result.Message;
    }
}
#pragma warning restore CS0618
