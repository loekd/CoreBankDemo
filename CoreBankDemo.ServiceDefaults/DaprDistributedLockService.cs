using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace CoreBankDemo.ServiceDefaults;

#pragma warning disable CS0618
#pragma warning disable DAPR_DISTRIBUTEDLOCK

/// <summary>
/// Dapr-backed <see cref="IDistributedLockService"/>. Reaches
/// <see cref="DaprClient"/> only here (AD-6), through Dapr's distributed-lock
/// API (<c>Lock</c>/<c>Unlock</c>), using a hardcoded lock store name and a
/// unique per-call owner token.
/// </summary>
/// <remarks>
/// Cooperative cancellation: once the lock is acquired, the workload is given
/// its own token — linked to the ambient token — that self-cancels at 5/6 of
/// <c>lockExpirySeconds</c> (a safety margin before the lock itself expires).
/// That cutoff's timing math and scheduling are extracted into
/// <see cref="CooperativeLockCancellation"/>, driven by an injected
/// <see cref="TimeProvider"/> so the cutoff is provable in isolation without
/// waiting on a real clock (AD-7: no renewal mechanism exists or is wired;
/// this cutoff is the only expiry-lifetime safeguard).
/// <para>
/// Never throws: lock-not-acquired, any Dapr exception, the workload's own
/// exception, and the workload observing the 5/6 cutoff and cancelling
/// itself are all caught here and turned into <c>false</c>.
/// </para>
/// </remarks>
public sealed class DaprDistributedLockService(
    DaprClient daprClient,
    TimeProvider timeProvider,
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
            var lockResponse = await daprClient.Lock(
                LockStoreName,
                lockName,
                lockOwner,
                lockExpirySeconds,
                cancellationToken).ConfigureAwait(false);

            if (!lockResponse.Success)
            {
                logger.LogDebug("Failed to acquire lock {LockName}", lockName);
                return false;
            }

            logger.LogInformation("Acquired lock {LockName} with {ExpirySeconds}s expiry", lockName, lockExpirySeconds);

            using var cancellation = CooperativeLockCancellation.Start(timeProvider, cancellationToken, lockExpirySeconds);

            try
            {
                await workload(cancellation.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when
                (cancellation.Token is { IsCancellationRequested: true }
                && cancellationToken is not { IsCancellationRequested: true })
            {
                logger.LogWarning(
                    "Operations cancelled for lock {LockName} after {Timeout}s to prevent lock expiry",
                    lockName, cancellation.WorkTimeout.TotalSeconds);
                return false;
            }
            finally
            {
                // Cleanup must not be skipped just because the caller's ambient
                // token was cancelled mid-workload — an Unlock call made with an
                // already-cancelled token can abort before the RPC is attempted,
                // leaking the lock until it expires on its own.
                await daprClient.Unlock(LockStoreName, lockName, lockOwner, CancellationToken.None).ConfigureAwait(false);
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

#pragma warning restore DAPR_DISTRIBUTEDLOCK
#pragma warning restore CS0618
