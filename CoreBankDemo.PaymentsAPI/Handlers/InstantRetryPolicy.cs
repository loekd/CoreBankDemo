namespace CoreBankDemo.PaymentsAPI.Handlers;

/// <summary>Where a wait between instant-rail attempts came from (span event tag <c>source</c>).</summary>
internal enum InstantRetrySource
{
    RetryAfter,
    Backoff
}

/// <summary>
/// <see cref="Retry"/> false means "go to the cancel phase now, without
/// sleeping"; true means "sleep <see cref="Wait"/>, then attempt again".
/// </summary>
internal readonly record struct InstantRetryDecision(bool Retry, TimeSpan Wait, InstantRetrySource Source);

/// <summary>
/// The instant rail's retry arithmetic (ADR-024), kept pure so it is testable
/// without a clock, a lock or a forwarder. The window is a ceiling, not a
/// quota: a wait is made only when a useful attempt can still follow it.
/// </summary>
internal static class InstantRetryPolicy
{
    /// <summary>An attempt shorter than this is a wasted command that only widens the cancel phase's ambiguity.</summary>
    public static readonly TimeSpan MinUsefulAttempt = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan BackoffBase = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan BackoffCap = TimeSpan.FromMilliseconds(1000);

    /// <summary>±50 %: the wait is <c>base · 2^(attempt−1) · (1 ± 0.5)</c>, then capped.</summary>
    public const double BackoffJitter = 0.5;

    public static bool CanStartAttempt(TimeSpan remaining) => remaining >= MinUsefulAttempt;

    /// <param name="attempt">1-based number of the attempt that just failed.</param>
    /// <param name="retryAfter">The server's hint, when a 429/503 carried one.</param>
    /// <param name="remaining">Forward window left, measured after the failure.</param>
    /// <param name="jitterSample">A uniform sample in [0, 1); the caller owns the randomness.</param>
    public static InstantRetryDecision AfterFailure(
        int attempt, int maxAttempts, TimeSpan? retryAfter, TimeSpan remaining, double jitterSample)
    {
        if (attempt >= maxAttempts)
        {
            return GiveUp;
        }

        var (wait, source) = retryAfter is TimeSpan hint
            ? (hint, InstantRetrySource.RetryAfter)
            : (Backoff(attempt, jitterSample), InstantRetrySource.Backoff);

        return wait + MinUsefulAttempt <= remaining
            ? new InstantRetryDecision(true, wait, source)
            : GiveUp;
    }

    private static readonly InstantRetryDecision GiveUp = new(false, TimeSpan.Zero, InstantRetrySource.Backoff);

    private static TimeSpan Backoff(int attempt, double jitterSample)
    {
        var nominalMs = BackoffBase.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var jitteredMs = nominalMs * (1 - BackoffJitter + 2 * BackoffJitter * jitterSample);
        return TimeSpan.FromMilliseconds(Math.Min(jitteredMs, BackoffCap.TotalMilliseconds));
    }
}
