using Terminal.Gui.App;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>
/// The console's Terminal.Gui clipboard, copying through the terminal
/// emulator (OSC 52, see <see cref="TerminalClipboard"/>) instead of an OS
/// clipboard helper. Installed on the driver at start-up so that Ctrl+C in
/// any text view and the evidence pane's Copy button share one path that
/// works with no display server -- the sandbox and every SSH session.
///
/// <para>
/// Terminal.Gui picks its Unix clipboard whenever <c>xclip</c> is on the
/// PATH and reports it as supported; only at copy time does <c>xclip</c>
/// discover there is no display to hand the text to, and that failure is
/// swallowed. The operator pressed Ctrl+C, nothing happened, and nothing
/// said so. Every attempt here reports its outcome through
/// <see cref="Copied"/> so the status bar can show it.
/// </para>
/// </summary>
public sealed class Osc52Clipboard : ClipboardBase
{
    private readonly TextWriter _terminal;
    private readonly string? _termEnvironment;
    private readonly string? _tmuxEnvironment;
    private string _lastCopied = string.Empty;

    /// <param name="terminal">The console's own output stream; the sequence must reach the emulator.</param>
    /// <param name="termEnvironment">The <c>TERM</c> value, used to detect GNU screen.</param>
    /// <param name="tmuxEnvironment">The <c>TMUX</c> value; non-empty means tmux passthrough is needed.</param>
    public Osc52Clipboard(TextWriter terminal, string? termEnvironment, string? tmuxEnvironment)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        _terminal = terminal;
        _termEnvironment = termEnvironment;
        _tmuxEnvironment = tmuxEnvironment;
    }

    /// <summary>Raised after every copy attempt with the sentence for the status bar.</summary>
    public event Action<ClipboardCopyResult>? Copied;

    /// <inheritdoc/>
    public override bool IsSupported => true;

    /// <summary>
    /// Reading the terminal's clipboard back needs a round trip that most
    /// emulators prompt for or refuse outright, so paste inside the console
    /// returns what was last copied from the console.
    /// </summary>
    protected override string GetClipboardDataImpl() => _lastCopied;

    /// <inheritdoc/>
    protected override void SetClipboardDataImpl(string text)
    {
        var result = TerminalClipboard.Copy(text, _terminal, _termEnvironment, _tmuxEnvironment);
        if (result.Succeeded)
        {
            _lastCopied = text;
        }

        Copied?.Invoke(result);

        if (!result.Succeeded)
        {
            // TrySetClipboardData swallows only NotSupportedException and
            // reports false; any other exception type would escape through a
            // Ctrl+C key press and take the console down with it.
            throw new NotSupportedException(result.Message);
        }
    }
}
