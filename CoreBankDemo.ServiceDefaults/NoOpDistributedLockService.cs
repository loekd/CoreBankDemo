namespace CoreBankDemo.ServiceDefaults;

/// <summary>
/// No-op implementation used by services that don't participate in distributed locking (e.g. read-only support APIs).
/// </summary>
internal sealed class NoOpDistributedLockService : IDistributedLockService
{
    public Task<bool> ExecuteWithLockAsync(
        string lockName,
        int lockExpirySeconds,
        Func<CancellationToken, Task> workload,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

