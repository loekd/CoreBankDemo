namespace CoreBankDemo.ServiceDefaults;

/// <summary>
/// Port for executing a workload while holding a distributed, expiry-based
/// lock (AD-6: infrastructure reached only through this port,
/// <see cref="Dapr.Client.DaprClient"/>, never elsewhere). Locks are never
/// renewed (AD-7) — a caller-chosen <c>lockExpirySeconds</c> is the only
/// lifetime an implementation has to work with.
/// </summary>
/// <remarks>
/// Story 3.2: this signature is fixed by epic 3's Legacy Behavioral Reference.
/// <c>CoreBankDemo.Messaging</c>'s <c>InboxProcessorBase</c>/<c>OutboxProcessorBase</c>
/// (epic 2, already merged and tested — 153 passing tests) compile against and
/// call this exact signature today; changing it without updating every
/// Messaging call site in the same commit would silently regress an
/// already-accepted epic. See <c>IDistributedLockServiceSignatureTests</c> for
/// the permanent reflection guard, and the story's Verification section for
/// building <c>CoreBankDemo.Messaging.csproj</c> unmodified as the real proof.
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
    /// <c>false</c> if the lock was not acquired, the workload observed a
    /// cooperative-cancellation cutoff, or any other failure occurred.
    /// Acquisition failure and every internal exception are reported this
    /// way — this method never throws.
    /// </returns>
    Task<bool> ExecuteWithLockAsync(
        string lockName,
        int lockExpirySeconds,
        Func<CancellationToken, Task> workload,
        CancellationToken cancellationToken = default);
}
