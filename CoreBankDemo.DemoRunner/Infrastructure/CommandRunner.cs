using System.ComponentModel;
using System.Diagnostics;

namespace CoreBankDemo.DemoRunner.Infrastructure;

public sealed class CommandRunner : ICommandRunner
{
    public async Task<CommandOutput> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return CommandOutput.Missing(ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await TerminateAsync(process);
            return CommandOutput.Timeout(Bound(await stdoutTask), Bound(await stderrTask));
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process);
            throw;
        }

        return new CommandOutput(
            process.ExitCode,
            Bound(await stdoutTask),
            Bound(await stderrTask),
            true,
            false,
            false);
    }

    private static async Task TerminateAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static string Bound(string value)
    {
        const int maximumLength = 64 * 1024;
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }
}
