using Medallion.Threading;
using Medallion.Threading.Redis;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CoreBankDemo.ServiceDefaults;

/// <summary>
/// Redis-backed <see cref="IDistributedLockService"/> (ADR-011, story 6.2):
/// replaces the Dapr lock adapter with <c>DistributedLock.Redis</c> over the
/// Aspire-managed <c>redis</c> resource's <see cref="IConnectionMultiplexer"/>.
/// Reaches Redis only through <see cref="IRedisDistributedLockFactory"/> (AD-6).
/// </summary>
/// <remarks>
/// Acquisition is non-blocking by default (<see cref="IDistributedLock.TryAcquireAsync"/>
/// with a zero timeout): a busy partition is skipped immediately rather than
/// queued. The bounded overload passes its timeout straight through, so an
/// inline instant-rail attempt can queue for a busy partition for at most
/// that long. While the returned handle is healthy, the library automatically
/// extends the Redis lease — there is no 5/6-of-expiry cooperative cutoff
/// (that mechanism belonged only to the superseded Dapr adapter, story 3.2).
/// The workload instead receives a token linked to both the caller's ambient
/// token and the handle's <see cref="IDistributedSynchronizationHandle.HandleLostToken"/>,
/// so losing ownership (e.g. a missed renewal) cancels cooperative work
/// promptly. The handle is always released via <c>await using</c>.
/// <para>
/// Never throws: acquisition failure, lock-loss cancellation, caller
/// cancellation, the workload's own exception, and any Redis/connection
/// failure (including on release) are all caught here and turned into
/// <c>false</c> — preserving the same never-throw boundary the Dapr adapter
/// had.
/// </para>
/// </remarks>
internal sealed class RedisDistributedLockService(
    IRedisDistributedLockFactory lockFactory,
    ILogger<RedisDistributedLockService> logger) : IDistributedLockService
{
    /// <summary>
    /// Application namespace prepended to every caller-supplied lock name
    /// before it reaches Redis, so this demo's lock keys can't collide with
    /// unrelated keys in the shared Redis instance. Added once, here — never
    /// recomputed differently by individual callers (design notes).
    /// </summary>
    internal const string LockKeyPrefix = "corebankdemo:lock:";

    public Task<bool> ExecuteWithLockAsync(
        string lockName,
        int lockExpirySeconds,
        Func<CancellationToken, Task> workload,
        CancellationToken cancellationToken = default)
        => ExecuteWithLockAsync(lockName, lockExpirySeconds, TimeSpan.Zero, workload, cancellationToken);

    public async Task<bool> ExecuteWithLockAsync(
        string lockName,
        int lockExpirySeconds,
        TimeSpan acquireTimeout,
        Func<CancellationToken, Task> workload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var redisLock = lockFactory.CreateLock(LockKeyPrefix + lockName, TimeSpan.FromSeconds(lockExpirySeconds));
            var handle = await redisLock.TryAcquireAsync(acquireTimeout, cancellationToken).ConfigureAwait(false);

            if (handle is null)
            {
                logger.LogDebug("Failed to acquire lock {LockName}", lockName);
                return false;
            }

            logger.LogInformation("Acquired lock {LockName} with {ExpirySeconds}s lease", lockName, lockExpirySeconds);

            try
            {
                // Linked, not the ambient token itself: losing Redis ownership
                // must cancel the workload without ever touching the caller's
                // own token, exactly as the ambient token cancelling must not
                // be mistaken for a lock-loss event below.
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, handle.HandleLostToken);

                try
                {
                    await workload(linkedCancellation.Token).ConfigureAwait(false);

                    // The workload may finish without ever observing cancellation
                    // even though ownership was lost concurrently (e.g. it doesn't
                    // check the token, or it raced the loss). Never report success
                    // for work that ran without a guaranteed-exclusive lock.
                    if (handle.HandleLostToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Lock {LockName} ownership was lost during the workload; not reporting success", lockName);
                        return false;
                    }

                    return true;
                }
                catch (OperationCanceledException) when (handle.HandleLostToken.IsCancellationRequested)
                {
                    logger.LogWarning("Lock {LockName} ownership was lost; workload cancelled", lockName);
                    return false;
                }
            }
            finally
            {
                // Cleanup must not be skipped just because the caller's ambient
                // token was cancelled mid-workload, or because ownership was
                // already lost — the handle is disposed unconditionally so a
                // still-valid lease is always released promptly.
                await handle.DisposeAsync().ConfigureAwait(false);
                logger.LogInformation("Released lock {LockName}", lockName);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Lock {LockName} operation cancelled by the caller", lockName);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to acquire or process with lock {LockName}", lockName);
            return false;
        }
    }
}

/// <summary>
/// Small internal seam (design notes) isolating <c>DistributedLock.Redis</c>
/// lock construction from <see cref="RedisDistributedLockService"/> so unit
/// tests can substitute a fake <see cref="IDistributedLock"/>/
/// <see cref="IDistributedSynchronizationHandle"/> pair instead of driving a
/// real Redis connection.
/// </summary>
internal interface IRedisDistributedLockFactory
{
    /// <summary>Creates a lock for the given fully-namespaced Redis key with the given lease duration.</summary>
    IDistributedLock CreateLock(string redisLockName, TimeSpan expiry);
}

/// <summary>
/// Production <see cref="IRedisDistributedLockFactory"/>: builds a
/// <see cref="RedisDistributedLock"/> per call against the shared
/// <see cref="IConnectionMultiplexer"/> Aspire injects for the <c>redis</c>
/// resource, configuring only the lease expiry — every other
/// <c>DistributedLock.Redis</c> option (extension cadence, busy-wait timing)
/// is left at the library's default per the design notes.
/// </summary>
internal sealed class RedisDistributedLockFactory(IConnectionMultiplexer connectionMultiplexer) : IRedisDistributedLockFactory
{
    public IDistributedLock CreateLock(string redisLockName, TimeSpan expiry) =>
        new RedisDistributedLock(
            redisLockName,
            connectionMultiplexer.GetDatabase(),
            options => options.Expiry(expiry));
}
