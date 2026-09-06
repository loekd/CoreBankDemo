using System.Globalization;

namespace CoreBankDemo.DemoRunner.Infrastructure;

public interface IStaleSidecarReaper
{
    /// <summary>
    /// Terminates any <c>daprd</c> left over from an earlier console run, returning the process
    /// ids actually reaped.
    /// </summary>
    Task<IReadOnlyList<int>> ReapAsync(string appId, CancellationToken ct);
}

/// <summary>
/// Kills <c>daprd</c> processes orphaned by an earlier run of this console.
/// </summary>
/// <remarks>
/// <para>
/// An orphan is not merely untidy. Dapr derives the Redis consumer group from the app-id, so a
/// leftover sidecar sits in the console's own group and Redis hands it a share of every
/// <c>transaction-events</c> delivery. Nothing is reading that share -- the console that spawned
/// it is gone -- so those outcomes are consumed and dropped. The new console reports
/// <c>Listening</c>, quite truthfully, while a portion of its payments never resolve and sit at
/// "Awaiting settlement" for ever. That is the console's most dangerous failure: it looks like
/// the bank is slow when in fact the observer is broken.
/// </para>
/// <para>
/// Orphans are routine rather than exotic. <c>Main</c>'s <c>await using</c> runs on an orderly
/// exit, but not when the console is SIGTERMed, SIGKILLed, or has its terminal closed underneath
/// it -- so every abrupt end to a rehearsal leaves one behind, and they accumulate silently.
/// </para>
/// <para>
/// This is the one place the codebase's "terminate only the exact PID this session spawned" rule
/// is deliberately relaxed, and it is narrowed to stay honest: the match is on the literal
/// <c>--app-id &lt;appId&gt;</c> argument, not on the process name, not on a port, and not on
/// <c>daprd</c> alone. That app-id belongs to this console by construction -- it exists precisely
/// because it must differ from every banking service's -- so the match cannot select an AppHost
/// sidecar, and a machine with no previous console run matches nothing at all.
/// </para>
/// </remarks>
public sealed class StaleSidecarReaper(
    ICommandRunner commands,
    IOwnedProcessTerminator terminator) : IStaleSidecarReaper
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GracefulWait = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<int>> ReapAsync(string appId, CancellationToken ct)
    {
        var listed = await ListProcessesAsync(ct);
        if (!listed.Succeeded)
        {
            // A machine whose process list cannot be read is not a reason to refuse to start:
            // the feed still works, it is only the orphan cleanup that is unavailable.
            return [];
        }

        var reaped = new List<int>();
        foreach (var pid in Parse(listed.StandardOutput, appId, Environment.ProcessId))
        {
            try
            {
                await terminator.EnsureExitedAsync(pid, GracefulWait, ct);
                reaped.Add(pid);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Another owner may have reaped it between the listing and the kill, and a
                // process this console cannot signal is not worth failing a start over.
            }
        }

        return reaped;
    }

    /// <summary>
    /// Selects the process ids whose command line carries the exact <c>--app-id &lt;appId&gt;</c>
    /// argument, never matching this console's own process.
    /// </summary>
    internal static IReadOnlyList<int> Parse(string psOutput, string appId, int currentProcessId)
    {
        // The argument as daprd is actually launched with it. Matching the flag together with
        // its value is what keeps a banking sidecar, which carries a different app-id, out.
        var needle = $"--app-id {appId}";
        var pids = new List<int>();
        foreach (var line in psOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf(' ', StringComparison.Ordinal);
            if (separator <= 0
                || !int.TryParse(
                    trimmed.AsSpan(0, separator),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var pid))
            {
                continue;
            }

            var command = trimmed[(separator + 1)..].TrimStart();
            if (!IsDaprd(command) || !HasArgument(command, needle))
            {
                continue;
            }

            if (pid == currentProcessId || pids.Contains(pid))
            {
                continue;
            }

            pids.Add(pid);
        }

        return pids;
    }

    /// <summary>
    /// Whether the process actually is <c>daprd</c>, judged by its executable rather than by
    /// anything later on the line. Without this, any process whose arguments merely quote the
    /// string -- a <c>ps | grep</c>, a <c>pkill -f</c>, a shell running either -- would be a
    /// candidate for termination, which is a far worse bug than the one being fixed.
    /// </summary>
    private static bool IsDaprd(string commandLine)
    {
        var end = commandLine.IndexOf(' ', StringComparison.Ordinal);
        var executable = end < 0 ? commandLine : commandLine[..end];
        var name = executable.AsSpan(executable.LastIndexOfAny(['/', '\\']) + 1);

        return name.Equals("daprd", StringComparison.Ordinal)
            || name.Equals("daprd.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the command line carries <paramref name="needle"/> as a whole argument. A plain
    /// substring test is not safe here: <c>--app-id demorunner-console</c> is a prefix of
    /// <c>--app-id demorunner-console-loadtest</c>, so it would reap a differently-named
    /// console's sidecar the day one exists. The value must end at a space or at end of line.
    /// </summary>
    private static bool HasArgument(string commandLine, string needle)
    {
        var from = 0;
        while (true)
        {
            var at = commandLine.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            var end = at + needle.Length;
            if (end == commandLine.Length || char.IsWhiteSpace(commandLine[end]))
            {
                return true;
            }

            from = at + 1;
        }
    }

    /// <summary>
    /// <c>ps -axww</c> rather than <c>/proc</c>: it is the one listing that reads full,
    /// untruncated command lines identically on macOS and Linux, and <c>-ww</c> is what stops
    /// the match silently failing on a command line wider than the terminal.
    /// </summary>
    private Task<CommandOutput> ListProcessesAsync(CancellationToken ct) =>
        commands.RunAsync(
            "ps",
            ["-axww", "-o", "pid=,command="],
            Environment.CurrentDirectory,
            ListTimeout,
            ct);
}
