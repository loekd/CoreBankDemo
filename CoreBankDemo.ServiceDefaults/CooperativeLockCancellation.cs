namespace CoreBankDemo.ServiceDefaults;

/// <summary>
/// Extracts <see cref="DaprDistributedLockService"/>'s 5/6-of-lock-lifetime
/// cooperative-cancellation mechanism into a pure, <see cref="TimeProvider"/>-driven
/// seam. It is provable in isolation — without a real
/// <see cref="Dapr.Client.DaprClient"/> and without waiting on a real clock in
/// tests, since a fake <see cref="TimeProvider"/> can simply advance its own
/// notion of "now" to fire the scheduled cutoff deterministically.
/// </summary>
internal static class CooperativeLockCancellation
{
    /// <summary>
    /// The point in a lock's lifetime, relative to acquisition, at which
    /// cooperative cancellation should fire: 5/6 of
    /// <paramref name="lockExpirySeconds"/>. For a 30s lock this is 25s to
    /// work, leaving a 5s safety margin before the lock itself expires. Pure
    /// arithmetic — no clock, no I/O.
    /// </summary>
    public static TimeSpan ComputeWorkTimeout(int lockExpirySeconds) =>
        TimeSpan.FromSeconds(lockExpirySeconds * 5.0 / 6.0);

    /// <summary>
    /// Starts a cooperative-cancellation scope: a token linked to
    /// <paramref name="ambientToken"/> that additionally self-cancels once
    /// <paramref name="timeProvider"/> reports that <see cref="ComputeWorkTimeout"/>
    /// has elapsed. The timer firing only cancels the scope's own linked
    /// token — <paramref name="ambientToken"/> itself is never touched.
    /// Callers must dispose the returned scope (it owns the underlying
    /// <see cref="CancellationTokenSource"/> and <see cref="ITimer"/>).
    /// </summary>
    public static CooperativeCancellationScope Start(
        TimeProvider timeProvider,
        CancellationToken ambientToken,
        int lockExpirySeconds)
    {
        var workTimeout = ComputeWorkTimeout(lockExpirySeconds);
        var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(ambientToken);
        var timer = timeProvider.CreateTimer(
            CancelSafely,
            linkedSource,
            workTimeout,
            Timeout.InfiniteTimeSpan);

        return new CooperativeCancellationScope(linkedSource, timer, workTimeout);
    }

    /// <summary>
    /// Timer callback: cancels the linked <see cref="CancellationTokenSource"/>
    /// passed as <paramref name="state"/>. Runs on its own thread and can race
    /// <see cref="CooperativeCancellationScope.Dispose"/> disposing the same
    /// source (workload already completed) — that race is expected, not an
    /// error, so a resulting <see cref="ObjectDisposedException"/> is swallowed
    /// rather than crashing the process on an unobserved background thread.
    /// </summary>
    internal static void CancelSafely(object? state)
    {
        var cts = (CancellationTokenSource)state!;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>
/// Disposable bundle returned by <see cref="CooperativeLockCancellation.Start"/>:
/// owns both the linked <see cref="CancellationTokenSource"/> and the
/// <see cref="TimeProvider"/>-driven timer that cancels it.
/// </summary>
internal sealed class CooperativeCancellationScope(
    CancellationTokenSource tokenSource,
    ITimer timer,
    TimeSpan workTimeout) : IDisposable
{
    /// <summary>Token that cancels on ambient cancellation or on the 5/6 cutoff, whichever comes first.</summary>
    public CancellationToken Token => tokenSource.Token;

    /// <summary>The duration, from scope start, after which the cutoff fires (5/6 of the lock's expiry).</summary>
    public TimeSpan WorkTimeout => workTimeout;

    public void Dispose()
    {
        timer.Dispose();
        tokenSource.Dispose();
    }
}
