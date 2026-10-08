using System.Globalization;
using System.Text.Json;
using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Terminal;

/// <summary>Which of the theme's existing tokens a line wears. No new colour is introduced.</summary>
public enum LineTone
{
    Neutral,
    Accent,
    Failure,
}

/// <summary>The Fetch result line: what came back on the left, when and how long on the right.</summary>
public sealed record FetchLineViewModel(string Status, string Stamp, LineTone Tone)
{
    public static FetchLineViewModel Empty { get; } = new(string.Empty, string.Empty, LineTone.Neutral);
}

/// <summary>
/// Projects the latest <see cref="PaymentStatusFetch"/> onto the Fetch result line (spec:
/// demorunner-operations-modes). The line persists until the next Fetch, and its stamp is the
/// press time, so a repeated identical answer still visibly changes.
/// </summary>
public static class PaymentStatusLine
{
    public const string FetchCaption = "Fetch";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static FetchLineViewModel Build(PaymentStatusFetch? fetch)
    {
        if (fetch is null)
        {
            return FetchLineViewModel.Empty;
        }

        if (fetch.Result is not { } result)
        {
            return new($"~ GET /api/payments/{fetch.TransactionId} …", string.Empty, LineTone.Neutral);
        }

        var stamp = $"fetched {OutcomeFeedNarrative.Clock(fetch.StartedAt)} · {result.Duration.TotalMilliseconds:F0} ms";
        return result.StatusCode switch
        {
            200 => TryRead(result.Body) is { } body
                ? new(
                    $"✓ 200  {body.Status} · {body.Amount.ToString("N2", CultureInfo.InvariantCulture)} {body.Currency} · "
                    + $"{(body.Status == "Pending" ? "since" : "at")} {OutcomeFeedNarrative.Clock(body.ProcessedAt)}",
                    stamp,
                    LineTone.Accent)
                : new("✗ 200  unreadable response body", stamp, LineTone.Failure),
            404 => new("○ 404  no payment with this id", stamp, LineTone.Neutral),
            > 0 => new($"✗ {result.StatusCode}  unexpected answer from PaymentsAPI", stamp, LineTone.Failure),
            // No status: either the request was never built (a refusal) or it got no answer.
            _ when result.Exchange is null => new($"✗ {result.ErrorSummary}", stamp, LineTone.Failure),
            _ => new($"✗ PaymentsAPI unreachable — {result.ErrorSummary}", stamp, LineTone.Failure),
        };
    }

    /// <summary>The Fetch button's caption: a running clock while its call is out, like <c>Cancelling — 3s</c>.</summary>
    public static string Caption(PaymentStatusFetch? fetch, DateTimeOffset now) =>
        fetch is { InFlight: true } inFlight
            ? $"Fetching — {Math.Max(0, (now - inFlight.StartedAt).TotalSeconds):F0}s"
            : FetchCaption;

    private static PaymentStatusBody? TryRead(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<PaymentStatusBody>(body, Web);
            return string.IsNullOrWhiteSpace(parsed?.Status) || string.IsNullOrWhiteSpace(parsed.Currency)
                ? null
                : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // PaymentsAPI's frozen PaymentResponse, as much of it as the line prints.
    private sealed record PaymentStatusBody(string? Status, decimal Amount, string? Currency, DateTimeOffset ProcessedAt);
}
