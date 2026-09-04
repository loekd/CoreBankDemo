using System.Diagnostics;

namespace CoreBankDemo.DemoRunner.Infrastructure;

public interface IOwnedProcessTerminator
{
    Task EnsureExitedAsync(int processId, TimeSpan gracefulWait, CancellationToken ct);
}

public sealed class OwnedProcessTerminator : IOwnedProcessTerminator
{
    public async Task EnsureExitedAsync(int processId, TimeSpan gracefulWait, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(gracefulWait);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
