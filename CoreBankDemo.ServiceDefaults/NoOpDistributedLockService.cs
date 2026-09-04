namespace CoreBankDemo.ServiceDefaults;

/// <summary>
/// No-op <see cref="IDistributedLockService"/> for services that don't
/// participate in distributed locking (e.g. LoadTestSupport's lock-free
/// hosting). Always reports "lock not acquired" — no lock is taken and the
/// workload never runs.
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
