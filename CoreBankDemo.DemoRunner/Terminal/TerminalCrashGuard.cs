using System.Diagnostics;
using System.Globalization;
using System.Text;
using Terminal.Gui.App;

namespace CoreBankDemo.DemoRunner.Terminal;

/// <summary>
/// Makes sure the operator gets their terminal, and an explanation, back when the console dies.
/// </summary>
/// <remarks>
/// <para>
/// A Terminal.Gui console switches the terminal into the alternate screen buffer, turns off
/// echo and canonical mode, hides the cursor and enables mouse reporting. Every one of those is
/// undone by <c>IApplication.Dispose</c> -- so any exit that does not reach it leaves the
/// operator staring at a dead full-screen view in a window that no longer responds to typing,
/// with no message saying what happened. On stage that is indistinguishable from the machine
/// having locked up.
/// </para>
/// <para>
/// The restore is therefore belt and braces. <c>IApplication.Dispose</c> is tried first because
/// it is the only thing that can put the terminal's own attributes back; but it can itself throw
/// or hang when the driver is already broken, which is exactly the situation a crash creates. So
/// the escape sequences are written unconditionally afterwards, and <c>stty sane</c> is run as a
/// last resort. Each step is independent: any one of them failing must not stop the others.
/// </para>
/// </remarks>
internal static class TerminalCrashGuard
{
    /// <summary>
    /// Leave the alternate screen, show the cursor, stop every mouse-reporting mode, stop
    /// bracketed paste, restore autowrap, and clear attributes. Written directly rather than
    /// through Terminal.Gui, because the point is to work when Terminal.Gui cannot.
    /// </summary>
    private const string Esc = "\u001b";

    private const string RestoreSequence =
        Esc + "[?1049l"                                    // leave the alternate screen
        + Esc + "[?25h"                                    // show the cursor
        + Esc + "[?1000l" + Esc + "[?1002l"                // stop mouse click/drag reporting
        + Esc + "[?1003l" + Esc + "[?1006l" + Esc + "[?1015l"
        + Esc + "[?2004l"                                  // stop bracketed paste
        + Esc + "[?7h"                                     // restore autowrap
        + Esc + "[0m";                                     // clear attributes

    private static readonly object Sync = new();
    private static readonly ManualResetEventSlim ReportFinished = new(false);
    private static string? _artifactsDirectory;
    private static bool _installed;
    private static bool _reported;
    private static IApplication? _application;

    /// <summary>
    /// Arms the guard for the rest of the process. Idempotent.
    /// </summary>
    /// <param name="artifactsDirectory">
    /// Gitignored directory the crash report is written to, so the operator can retrieve it
    /// after the terminal has been cleared.
    /// </param>
    internal static void Install(string artifactsDirectory)
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            _artifactsDirectory = artifactsDirectory;
            _installed = true;
        }

        // Reached when an exception escapes a thread the console does not own -- a timer
        // callback, a background poll, the outcome feed's own subscription thread. The runtime
        // is about to terminate the process and no `finally` anywhere will run, so this is the
        // only remaining chance to hand the terminal back.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(e.ExceptionObject as Exception, "An unhandled exception reached the runtime");

        // A faulted Task nobody awaited. It does not kill the process on modern .NET, so the
        // console keeps running -- but it is still a fault worth stating rather than swallowing,
        // and the terminal is deliberately left alone here.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            WriteReportFile("An unobserved task exception was raised", e.Exception);
        };
    }

    /// <summary>
    /// Records the live application instance so a crash on any thread can dispose it. Called by
    /// <c>Program.RunConsole</c> right after the instance is created.
    /// </summary>
    internal static void AttachApplication(IApplication application)
    {
        Volatile.Write(ref _application, application);
    }

    /// <summary>
    /// Atomically clears the recorded instance and returns whatever was there, or <c>null</c> if
    /// nothing was attached or it was already taken. Whichever of a crash (<see
    /// cref="RestoreTerminal"/>) or normal shutdown (<c>Program.RunConsole</c>'s <c>finally</c>)
    /// calls this first is the only one that gets a non-null result back — so exactly one of them
    /// ever disposes the instance, with no window for a double-dispose race between the two.
    /// </summary>
    internal static IApplication? TakeApplication()
    {
        return Interlocked.Exchange(ref _application, null);
    }

    /// <summary>
    /// Blocks until a report started on another thread has finished restoring the terminal and
    /// writing its explanation, or until <paramref name="timeout"/> elapses. Returns
    /// <c>false</c> at once if nothing was ever reported.
    /// </summary>
    /// <remarks>
    /// Disposing the application instance from the reporting thread ends the run loop on the UI
    /// thread, which then heads for the end of <c>Main</c>. Without this wait it gets there
    /// first, and the process exits with code 0 underneath a half-written report: no banner,
    /// no crash file, and an exit status that says nothing went wrong.
    /// </remarks>
    internal static bool WaitForReport(TimeSpan timeout)
    {
        lock (Sync)
        {
            if (!_reported)
            {
                return false;
            }
        }

        ReportFinished.Wait(timeout);
        return true;
    }

    /// <summary>
    /// Restores the terminal and states what happened, once. Safe to call from a signal
    /// handler, from a catch block, or from both.
    /// </summary>
    internal static void Report(Exception? exception, string headline)
    {
        lock (Sync)
        {
            if (_reported)
            {
                return;
            }

            _reported = true;
        }

        try
        {
            RestoreAndExplain(exception, headline);
        }
        finally
        {
            ReportFinished.Set();
        }
    }

    private static void RestoreAndExplain(Exception? exception, string headline)
    {
        RestoreTerminal();
        var path = WriteReportFile(headline, exception);

        // Deliberately stderr and deliberately plain: the terminal has just been reset, so
        // nothing here may assume colour, cursor addressing, or a working Terminal.Gui.
        var message = new StringBuilder()
            .Append('\a')
            .AppendLine()
            .AppendLine("┌──────────────────────────────────────────────────────────────────────┐")
            .AppendLine("│ The CoreBankDemo operator console stopped unexpectedly.              │")
            .AppendLine("└──────────────────────────────────────────────────────────────────────┘")
            .AppendLine($"  {headline}.")
            .AppendLine();

        if (exception is not null)
        {
            message.AppendLine($"  {exception.GetType().Name}: {exception.Message}");
            var frame = FirstOwnFrame(exception);
            if (frame is not null)
            {
                message.AppendLine($"  at {frame}");
            }

            message.AppendLine();
        }

        message
            .AppendLine("  Your terminal has been restored; you are back at the shell.")
            .AppendLine("  Any AppHost this console started is still running -- it is not stopped by a crash.");

        if (path is not null)
        {
            message.AppendLine($"  Full detail: {path}");
        }

        try
        {
            Console.Error.Write(message.ToString());
            Console.Error.Flush();
        }
        catch (IOException)
        {
            // stderr is gone as well; the terminal restore above is still the useful half.
        }
    }

    /// <summary>
    /// Hands the terminal back, trying every mechanism independently. Never throws.
    /// </summary>
    internal static void RestoreTerminal()
    {
        try
        {
            TakeApplication()?.Dispose();
        }
        catch (Exception)
        {
            // Expected when the driver is the thing that broke. The raw sequences below are
            // precisely the fallback for this case.
        }

        try
        {
            Console.Out.Write(RestoreSequence);
            Console.Out.Flush();
        }
        catch (IOException)
        {
            // Nothing further can be done through stdout.
        }

        RunSttySane();
    }

    /// <summary>
    /// Restores echo and canonical mode when Terminal.Gui could not. Without this the shell
    /// prompt returns but typing stays invisible, which reads as "still frozen".
    /// </summary>
    private static void RunSttySane()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var stty = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"stty sane < /dev/tty\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            stty?.WaitForExit(2000);
        }
        catch (Exception)
        {
            // No controlling terminal (the console was piped or run headless), or no shell.
            // Either way there is nothing left to restore.
        }
    }

    private static string? WriteReportFile(string headline, Exception? exception)
    {
        var directory = Volatile.Read(ref _artifactsDirectory);
        if (directory is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(directory);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(directory, $"crash-{stamp}.log");
            File.WriteAllText(
                path,
                $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{headline}.{Environment.NewLine}{Environment.NewLine}"
                + $"{exception?.ToString() ?? "(no exception was supplied)"}{Environment.NewLine}");
            return path;
        }
        catch (Exception)
        {
            // A crash report that cannot be written must never become a second crash.
            return null;
        }
    }

    /// <summary>
    /// The first stack frame belonging to this console, which is far more useful on stage than
    /// the top frame -- that is usually inside Terminal.Gui or the runtime.
    /// </summary>
    private static string? FirstOwnFrame(Exception exception) =>
        exception.StackTrace?
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Contains("CoreBankDemo.DemoRunner", StringComparison.Ordinal))?
            .TrimStart("at ".ToCharArray());
}
