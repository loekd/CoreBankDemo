using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace CoreBankDemo.ServiceDefaults;

#pragma warning disable CS0618

public class DaprDistributedLockService(
    DaprClient daprClient,
    ILogger<DaprDistributedLockService> logger) : IDistributedLockService
{
    private const string LockStoreName = "lockstore";

    public async Task<bool> ExecuteWithLockAsync(
        string lockName,
        int lockExpirySeconds,
        Func<CancellationToken, Task> workload,
        CancellationToken cancellationToken = default)
    {
        var lockOwner = $"{Environment.MachineName}-{Guid.NewGuid()}";

        try
        {
#pragma warning disable DAPR_DISTRIBUTEDLOCK
            var lockResponse = await daprClient.Lock(
                LockStoreName,
                lockName,
                lockOwner,
                lockExpirySeconds,
                cancellationToken);
#pragma warning restore DAPR_DISTRIBUTEDLOCK

            if (!lockResponse.Success)
            {
                logger.LogDebug("Failed to acquire lock {LockName}", lockName);
                return false;
            }

            logger.LogInformation("Acquired lock {LockName} with {ExpirySeconds}s expiry", lockName, lockExpirySeconds);

            // Cancel operations before lock expires (at 5/6 of expiry time to be safe)
            // For 30s lock, this gives 25s to work, 5s safety margin
            var workTimeout = TimeSpan.FromSeconds(lockExpirySeconds * 5.0 / 6.0);
            using var workCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            workCts.CancelAfter(workTimeout);

            try
            {
                await workload(workCts.Token);
                return true;
            }
            catch (OperationCanceledException) when 
                (workCts.Token is { IsCancellationRequested: true } 
                && cancellationToken is not { IsCancellationRequested: true })
            {
                logger.LogWarning(
                    "Operations cancelled for lock {LockName} after {Timeout}s to prevent lock expiry",
                    lockName, workTimeout.TotalSeconds);
                return false;
            }
            finally
            {
#pragma warning disable DAPR_DISTRIBUTEDLOCK
                await daprClient.Unlock(LockStoreName, lockName, lockOwner, cancellationToken);
#pragma warning restore DAPR_DISTRIBUTEDLOCK
                logger.LogInformation("Released lock {LockName}", lockName);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to acquire or process with lock {LockName}", lockName);
            return false;
        }
    }
}
#pragma warning restore CS0618

