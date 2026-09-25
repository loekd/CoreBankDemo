using System.Net.Http.Headers;

namespace CoreBankDemo.PaymentsAPI.Outbox;

/// <summary>
/// Reads a <c>Retry-After</c> response header (RFC 9110 §10.2.3) into a
/// wait, on the caller's <see cref="TimeProvider"/> so an HTTP-date form is
/// relative to the same clock the instant rail's budget runs on (ADR-024).
/// Deliberately ignorant of status codes: whether the header <em>counts</em>
/// is <see cref="KiotaCoreBankApiClient"/>'s decision.
/// </summary>
internal static class RetryAfterHeader
{
    public const string Name = "Retry-After";

    /// <summary>
    /// <see langword="true"/> with a non-negative wait when the header is
    /// present and well-formed; <see langword="false"/> when it is missing
    /// or unparseable (spec: treated as absent). An HTTP-date already in the
    /// past is a zero wait, not an absence.
    /// </summary>
    public static bool TryParse(
        IDictionary<string, IEnumerable<string>>? headers,
        TimeProvider timeProvider,
        out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (headers is null)
        {
            return false;
        }

        var value = headers
            .Where(header => string.Equals(header.Key, Name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value) || !RetryConditionHeaderValue.TryParse(value, out var parsed))
        {
            return false;
        }

        if (parsed.Delta is TimeSpan delta)
        {
            retryAfter = delta;
            return true;
        }

        // RetryConditionHeaderValue sets exactly one of Delta/Date on a
        // successful parse, and its delta-seconds grammar has no sign, so a
        // negative value never reaches this method.
        var until = parsed.Date!.Value - timeProvider.GetUtcNow();
        retryAfter = until < TimeSpan.Zero ? TimeSpan.Zero : until;
        return true;
    }
}
