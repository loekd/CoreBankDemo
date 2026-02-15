namespace CoreBankDemo.ServiceDefaults;

/// <summary>
/// Service for acquiring and maintaining distributed locks with automatic heartbeat
/// </summary>
public interface IDistributedLockService
{
    /// <summary>
    /// Executes an action while holding a distributed lock with automatic heartbeat renewal
    /// </summary>
    /// <param name="lockName">Name of the lock to acquire</param>
    /// <param name="workload">The work to perform while holding the lock</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if lock was acquired and work completed, false if lock was not acquired</returns>
    Task<bool> ExecuteWithLockAsync(
        string lockName,
        Func<CancellationToken, Task> workload,
        CancellationToken cancellationToken = default);
}
