using System.Text;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>Outcome of a copy attempt, carrying the sentence the status bar shows.</summary>
public sealed record ClipboardCopyResult(bool Succeeded, string Message);

/// <summary>
/// Copies text to the clipboard of the terminal emulator displaying the
/// console, using the OSC 52 escape sequence.
///
/// <para>
/// Terminal.Gui's own <c>Clipboard</c> shells out to an OS clipboard helper
/// (<c>xclip</c>/<c>xsel</c>/<c>wl-copy</c>/<c>pbcopy</c>) and silently does
/// nothing when none of them can reach a display server -- exactly the case
/// when the console runs in the sandbox or over SSH. <c>xclip</c> may well be
/// installed, but with no <c>DISPLAY</c> it cannot copy anything and the
/// failure never reaches the operator, which is what made the copy action
/// look like it did nothing at all. OSC 52 asks the *terminal* to set its own
/// clipboard instead, so it works wherever the session is being viewed
/// (Ghostty, iTerm2, WezTerm, kitty, recent xterm) with no helper binary and
/// no display server.
/// </para>
/// </summary>
public static class TerminalClipboard
{
    private const string Escape = "\u001b";
    private const string Bell = "\u0007";
    private const string StringTerminator = $"{Escape}\\";

    /// <summary>
    /// Terminals cap the OSC 52 payload; xterm's documented ceiling is just
    /// under 100 KB of base64 and most emulators sit at or below it. Evidence
    /// details are far smaller, so an oversized payload is refused with a
    /// pointer to the export button rather than silently truncated into a
    /// corrupt copy.
    /// </summary>
    public const int MaxBase64Length = 74_994;

    /// <param name="terminal">The console's own output stream; the sequence must reach the emulator, not a log.</param>
    /// <param name="termEnvironment">The <c>TERM</c> value, used only to detect GNU screen.</param>
    /// <param name="tmuxEnvironment">The <c>TMUX</c> value; non-empty means the sequence needs tmux passthrough.</param>
    public static ClipboardCopyResult Copy(
        string text,
        TextWriter terminal,
        string? termEnvironment = null,
        string? tmuxEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (string.IsNullOrEmpty(text))
        {
            return new ClipboardCopyResult(false, "There is nothing to copy.");
        }

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        if (encoded.Length > MaxBase64Length)
        {
            return new ClipboardCopyResult(
                false,
                $"Too large for the terminal clipboard ({text.Length:N0} characters) — use Export session evidence instead.");
        }

        terminal.Write(Wrap($"{Escape}]52;c;{encoded}{Bell}", termEnvironment, tmuxEnvironment));
        terminal.Flush();
        return new ClipboardCopyResult(
            true,
            $"Copied {text.Length:N0} characters to the terminal clipboard (OSC 52).");
    }

    /// <summary>
    /// tmux and GNU screen consume escape sequences rather than forwarding
    /// them, so the sequence has to be tunnelled through their DCS
    /// passthrough to reach the emulator that actually owns the clipboard.
    /// tmux additionally requires every inner escape to be doubled.
    /// </summary>
    private static string Wrap(string sequence, string? termEnvironment, string? tmuxEnvironment)
    {
        if (!string.IsNullOrEmpty(tmuxEnvironment))
        {
            var escaped = sequence.Replace(Escape, Escape + Escape, StringComparison.Ordinal);
            return $"{Escape}Ptmux;{escaped}{StringTerminator}";
        }

        return termEnvironment?.StartsWith("screen", StringComparison.Ordinal) == true
            ? $"{Escape}P{sequence}{StringTerminator}"
            : sequence;
    }
}
