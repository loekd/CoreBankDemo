namespace CoreBankDemo.ServiceDefaults;

/// <summary>
/// Port for executing a workload while holding a distributed, expiry-based
/// lock (AD-6: infrastructure reached only through this port). The
/// production adapter is <see cref="RedisDistributedLockService"/>
/// (ADR-011, story 6.2), which acquires a Redis lease via
/// <c>DistributedLock.Redis</c> and automatically renews it while the
/// workload is healthy — <c>lockExpirySeconds</c> configures that lease's
/// duration, not a hard cutoff. Ownership loss (e.g. a missed renewal) or
/// caller cancellation stops the workload cooperatively via its token; see
/// <see cref="RedisDistributedLockService"/>'s remarks.
/// </summary>
/// <remarks>
/// Story 3.2 fixed this signature against epic 3's Legacy Behavioral
/// Reference and it has not changed since:
/// <c>CoreBankDemo.Messaging</c>'s <c>InboxProcessorBase</c>/<c>OutboxProcessorBase</c>
/// compile against and call this exact signature. Changing it without
/// updating every Messaging call site in the same commit would silently
/// regress already-accepted epics. See <c>IDistributedLockServiceSignatureTests</c>
/// for the permanent reflection guard, and this story's Verification section
/// for building <c>CoreBankDemo.Messaging.csproj</c> unmodified as the real
/// proof.
/// </remarks>
public interface IDistributedLockService
{
    /// <summary>
    /// Attempts to acquire <paramref name="lockName"/> for up to
    /// <paramref name="lockExpirySeconds"/> and, if acquired, runs
    /// <paramref name="workload"/> while holding it.
    /// </summary>
    /// <param name="lockName">Name of the lock to acquire.</param>
    /// <param name="lockExpirySeconds">Seconds the lock is held before it expires.</param>
    /// <param name="workload">The work to perform while holding the lock.</param>
    /// <param name="cancellationToken">Ambient cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the lock was acquired and the workload completed;
    /// <c>false</c> if the lock was not acquired, the caller cancelled, lock
    /// ownership was lost mid-workload, or any other failure occurred.
    /// Acquisition failure and every internal exception are reported this
    /// way — this method never throws.
    /// </returns>
    Task<bool> ExecuteWithLockAsync(
        string lockName,
        int lockExpirySeconds,
        Func<CancellationToken, Task> workload,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="ExecuteWithLockAsync(string,int,Func{CancellationToken,Task},CancellationToken)"/>
    /// but waits up to <paramref name="acquireTimeout"/> for a busy lock
    /// instead of skipping it immediately. The background processors keep the
    /// non-blocking form (a busy partition is simply the next tick's work);
    /// the instant rail's inline paths use this one, because under load a
    /// busy partition lock is the *normal* case and an SCT Inst has a budget
    /// precisely so that it can wait a bounded time rather than give up.
    /// The default implementation ignores the timeout and behaves like the
    /// non-blocking form, so existing implementations keep working unchanged.
    /// </summary>
    Task<bool> ExecuteWithLockAsync(
        string lockName,
        int lockExpirySeconds,
        TimeSpan acquireTimeout,
        Func<CancellationToken, Task> workload,
        CancellationToken cancellationToken = default)
        => ExecuteWithLockAsync(lockName, lockExpirySeconds, workload, cancellationToken);
}
