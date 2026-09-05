using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Application;

/// <summary>
/// The one place the feed's own wording is written.
/// <para>
/// The controller announces a feed transition in Evidence and the presentation model states the
/// same fact in the feed header. Two copies of that wording drift — they already had, one
/// pluralising "payments" and the other not — and a console whose two surfaces disagree about
/// how many outcomes it cannot account for is exactly the kind of small lie this feature exists
/// to remove.
/// </para>
/// </summary>
public static class OutcomeFeedNarrative
{
    /// <summary>The remedy named wherever the console cannot say what happened.</summary>
    public const string OutcomeQueryRemedy = "Query outcome is the way forward.";

    /// <summary>Renders a clock, or says plainly that it is not known. Never an empty gap.</summary>
    public static string Clock(DateTimeOffset? value) =>
        value is { } moment ? moment.ToString("HH:mm:ss") : "an unrecorded time";

    public static string PreciseClock(DateTimeOffset? value) =>
        value is { } moment ? moment.ToString("HH:mm:ss.fff") : "an unrecorded time";

    public static string ListeningSince(DateTimeOffset? since) =>
        $"Listening since {Clock(since)} — events before this time were not observed";

    public static string ListeningAgain(DateTimeOffset? gapStart, DateTimeOffset? gapEnd) =>
        $"Listening again — no events observed {Clock(gapStart)}–{Clock(gapEnd)}";

    public static string FeedLost(DateTimeOffset? lostAt, int unknownCount) =>
        $"Feed lost {Clock(lostAt)} — {Count(unknownCount, "payment")} "
        + $"{(unknownCount == 1 ? "has" : "have")} unknown outcomes. {OutcomeQueryRemedy}";

    public static string Unavailable(string detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? $"Outcome not observed — no feed. {OutcomeQueryRemedy}"
            : $"Outcome not observed — no feed. {detail.Trim()} {OutcomeQueryRemedy}";

    public static string NotStarted() =>
        "No outcome feed — start or attach a topology. Query outcome is always available.";

    public static string ReconnectExhausted(int attempts) =>
        $"Outcome feed not re-established after {Count(attempts, "attempt")} — the console has stopped "
        + $"retrying. {OutcomeQueryRemedy}";

    private static string Count(int value, string noun) =>
        $"{value} {noun}{(value == 1 ? string.Empty : "s")}";
}
