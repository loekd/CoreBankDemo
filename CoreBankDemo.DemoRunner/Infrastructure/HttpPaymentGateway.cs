using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

public sealed class HttpPaymentGateway(HttpClient httpClient) : IPaymentGateway
{
    private const int BodyExcerptLength = 200;

    /// <summary>
    /// How many values of one repeated header are joined into its recorded line. HTTP allows
    /// arbitrarily many; the recorded value is bounded again by <see cref="JournalText"/>, and
    /// this stops the join itself from building the string that would then be thrown away.
    /// </summary>
    private const int MaximumHeaderValues = 16;

    public async Task<PaymentResult> SubmitAsync(
        TopologyProfile profile,
        PaymentSubmission submission,
        CancellationToken ct)
    {
        var (url, _) = EndpointResolver.EndpointFor(profile, KnownEndpoints.PaymentsSubmit);
        var payload = JsonSerializer.Serialize(new
        {
            submission.Request.FromAccount,
            submission.Request.ToAccount,
            submission.Request.Amount,
            submission.Request.Currency,
            Scheme = submission.Request.Rail == PaymentRail.Instant ? "instant" : "standard",
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (submission.IdempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", submission.IdempotencyKey);
        }

        // Captured before the send, from the message actually about to go out: the headers on
        // the record are the headers that were set, never a re-derivation of what they should
        // have been. In Omitted mode there is simply no key header here, and that absence is
        // the fact the demo turns on.
        var sent = Requested(request, payload);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();
            var parsed = ParsePaymentResponse(body);
            var violation = DescribeViolation(response.StatusCode, parsed, submission.IdempotencyKey, body);
            return new PaymentResult(
                violation is null ? MapOutcome(parsed.Status) : PaymentOutcome.TransportFailure,
                (int)response.StatusCode,
                parsed.PaymentId,
                parsed.TransactionId,
                parsed.Status,
                body,
                violation,
                stopwatch.Elapsed,
                Answered(sent, response, body));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new PaymentResult(
                PaymentOutcome.Ambiguous,
                0,
                null,
                submission.IdempotencyKey,
                null,
                null,
                "Request timed out; the server may have accepted it.",
                stopwatch.Elapsed,
                // A request that got no answer is still evidence, and the exchange says which
                // half is missing by leaving StatusCode null.
                sent);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return new PaymentResult(
                submission.IdempotencyMode == IdempotencyMode.Omitted
                    ? PaymentOutcome.Ambiguous
                    : PaymentOutcome.TransportFailure,
                0,
                null,
                submission.IdempotencyKey,
                null,
                null,
                $"Could not reach PaymentsAPI: {ex.Message}",
                stopwatch.Elapsed,
                sent);
        }
    }

    /// <summary>
    /// The request half of an exchange, taken off the message that is about to be sent. Both
    /// header collections are merged so <c>Content-Type</c> lands beside <c>Idempotency-Key</c>,
    /// exactly as a tool that replays the call would show them.
    /// </summary>
    private static HttpExchange Requested(HttpRequestMessage request, string? body) => new(
        request.Method.Method,
        request.RequestUri?.ToString() ?? string.Empty,
        Headers(request.Headers, request.Content?.Headers),
        body is null ? null : JournalText.Bound(body),
        null,
        null,
        [],
        null);

    /// <summary>Fills the response half in. Called only when an answer actually arrived.</summary>
    private static HttpExchange Answered(HttpExchange sent, HttpResponseMessage response, string body) =>
        sent with
        {
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            ResponseHeaders = Headers(response.Headers, response.Content.Headers),
            ResponseBody = JournalText.Bound(body),
        };

    /// <summary>
    /// Merges the message's own headers with its content's, so <c>Content-Type</c> lands beside
    /// the headers this console set. Bounded on the way out in both directions: a remote is not
    /// allowed to decide how many lines the pane draws, nor how wide one of them is.
    /// </summary>
    private static IReadOnlyList<EvidenceHeader> Headers(
        params IEnumerable<KeyValuePair<string, IEnumerable<string>>>?[] collections) =>
        JournalText.Bound(collections
            .Where(collection => collection is not null)
            .SelectMany(collection => collection!)
            .Select(header => new EvidenceHeader(
                header.Key,
                string.Join(", ", header.Value.Take(MaximumHeaderValues)))));

    /// <summary>
    /// Withdraws a payment through CoreBank's own cancellation endpoint. The mapping is driven by
    /// the body's status word rather than by the HTTP code alone, exactly as
    /// <see cref="MapOutcome"/> is: <c>200 Cancelled</c> is a withdrawal, a <c>200</c> carrying a
    /// committed status is the bank saying it was too late, a <c>409</c> carries the row's current
    /// status, and everything else — any other code, an unreadable body, a timeout, a dead
    /// connection — asserts nothing about the payment.
    /// </summary>
    public async Task<PaymentCancellationResult> CancelAsync(
        TopologyProfile profile,
        PaymentCancellation cancellation,
        CancellationToken ct)
    {
        var (url, method) = EndpointResolver.EndpointFor(profile, KnownEndpoints.TransactionCancel);
        var payload = JsonSerializer.Serialize(new
        {
            cancellation.FromAccount,
            cancellation.ToAccount,
            cancellation.Amount,
            cancellation.Currency,
            cancellation.TransactionId,
        });
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        var sent = Requested(request, payload);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();
            var (status, processedAt) = ReadCancelResponse(body);
            var code = (int)response.StatusCode;
            var exchange = Answered(sent, response, body);

            if (string.IsNullOrWhiteSpace(status))
            {
                // A 400 carries the bank's own validation errors instead of a status; naming them
                // is the difference between a reason the operator can act on and a bare code.
                var errors = ReadErrors(body);
                return new PaymentCancellationResult(
                    PaymentCancelOutcome.TransportFailure,
                    code,
                    cancellation.TransactionId,
                    null,
                    body,
                    errors is { Length: > 0 }
                        ? $"CoreBankAPI refused the cancel with HTTP {code}: {errors}"
                        : $"CoreBankAPI answered the cancel with HTTP {code} and no readable status{Excerpt(body)}",
                    stopwatch.Elapsed,
                    Exchange: exchange);
            }

            var outcome = MapOutcome(status);
            return (code, outcome) switch
            {
                (200, PaymentOutcome.Cancelled) => Result(PaymentCancelOutcome.Cancelled, null),
                // 200 carrying a committed status: the bank had already executed it, and says so
                // with the response it committed. Never re-labelled as a cancellation.
                (200, PaymentOutcome.Completed or PaymentOutcome.Failed) =>
                    Result(PaymentCancelOutcome.AlreadyCommitted, null),
                // A 409 whose body says Cancelled is the bank stating the row is already
                // withdrawn. Unreachable from the real server today, but the console must never
                // leave a payment open when the bank has said it is not.
                (409, PaymentOutcome.Cancelled) => Result(PaymentCancelOutcome.Cancelled, null),
                (409, not PaymentOutcome.TransportFailure) => Result(PaymentCancelOutcome.Refused, null),
                _ => Result(
                    PaymentCancelOutcome.TransportFailure,
                    $"CoreBankAPI answered the cancel with HTTP {code} and status '{status}'{Excerpt(body)}"),
            };

            PaymentCancellationResult Result(PaymentCancelOutcome mapped, string? error) =>
                new(mapped, code, cancellation.TransactionId, status, body, error, stopwatch.Elapsed, processedAt, exchange);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new PaymentCancellationResult(
                PaymentCancelOutcome.TransportFailure,
                0,
                cancellation.TransactionId,
                null,
                null,
                "The cancel request timed out; the payment is left exactly as it was.",
                stopwatch.Elapsed,
                Exchange: sent);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return new PaymentCancellationResult(
                PaymentCancelOutcome.TransportFailure,
                0,
                cancellation.TransactionId,
                null,
                null,
                $"Could not reach CoreBankAPI to cancel: {ex.Message}",
                stopwatch.Elapsed,
                Exchange: sent);
        }
    }

    /// <summary>
    /// The cancel response is a bare <c>TransactionResponse</c> — no <c>paymentId</c> — so the
    /// submission parser's malformed rule does not apply to it. The status word carries the
    /// business meaning and <c>processedAt</c> the bank's own clock for it; a body that has
    /// neither is not a cancel answer at all.
    /// </summary>
    /// <summary>
    /// The <c>errors</c> array a validation refusal answers with, joined for reading. Named
    /// rather than dumped: "the same error" on a second run is otherwise indistinguishable from
    /// the first, which is exactly what makes a bare code hard to act on.
    /// </summary>
    private static string? ReadErrors(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "errors", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Array)
                {
                    return string.Join(" ", property.Value.EnumerateArray().Select(value => value.ToString()));
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string? Status, DateTimeOffset? ProcessedAt) ReadCancelResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var stamp = ReadString(document.RootElement, "processedAt");
            return (
                ReadString(document.RootElement, "status"),
                DateTimeOffset.TryParse(
                    stamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var processedAt)
                    ? processedAt
                    : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    public Task<InspectionResult> QueryOutcomeAsync(
        TopologyProfile profile,
        string transactionIdOrKey,
        CancellationToken ct) =>
        SendInspectionAsync(profile, KnownEndpoints.TransactionOutcome, transactionIdOrKey, null, ct);

    public Task<InspectionResult> InspectAsync(
        TopologyProfile profile,
        string endpointId,
        CancellationToken ct) =>
        SendInspectionAsync(profile, endpointId, null, null, ct);

    private async Task<InspectionResult> SendInspectionAsync(
        TopologyProfile profile,
        string endpointId,
        string? pathParameter,
        IReadOnlyDictionary<string, string>? query,
        CancellationToken ct)
    {
        string url;
        HttpMethod method;
        try
        {
            (url, method) = EndpointResolver.EndpointFor(profile, endpointId, pathParameter);
        }
        catch (ArgumentException ex)
        {
            return new InspectionResult(false, 0, endpointId, null, ex.Message, TimeSpan.Zero);
        }

        if (query is { Count: > 0 })
        {
            url += "?" + string.Join(
                "&",
                query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        }

        // A bare request: no headers, no body. Its REQUEST column is a request line and nothing
        // else, which is honest and is what a tool replaying the call would show. Headers are
        // never synthesised to fill the column.
        using var request = new HttpRequestMessage(method, url);
        var sent = Requested(request, null);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();
            return new InspectionResult(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                endpointId,
                body,
                response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}",
                stopwatch.Elapsed,
                Answered(sent, response, body));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new InspectionResult(false, 0, endpointId, null, "Request timed out.", stopwatch.Elapsed, sent);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return new InspectionResult(false, 0, endpointId, null, ex.Message, stopwatch.Elapsed, sent);
        }
    }

    /// <summary>
    /// Reports why a response cannot be read as a payment acknowledgement, or
    /// <see langword="null"/> when it can. Every rejection names the offending
    /// value: "the same error" on a second run is otherwise indistinguishable
    /// from the first, which is exactly what made this hard to diagnose.
    /// </summary>
    private static string? DescribeViolation(
        HttpStatusCode statusCode,
        PaymentParseResult parsed,
        string? idempotencyKey,
        string body)
    {
        // The instant rail's time-out rejection (ADR-020): 504 whose body says Cancelled is a
        // contractual answer -- the payment was withdrawn before it executed -- not an error.
        // A 504 carrying any other body is still what it looks like: a gateway failure.
        var isCancellation = statusCode == HttpStatusCode.GatewayTimeout
            && !parsed.IsMalformed
            && MapOutcome(parsed.Status) == PaymentOutcome.Cancelled;
        if (statusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices && !isCancellation)
        {
            return $"PaymentsAPI returned HTTP {(int)statusCode}{Excerpt(body)}";
        }

        if (parsed.IsMalformed)
        {
            return $"PaymentsAPI returned HTTP {(int)statusCode} without a paymentId/transactionId/status acknowledgement{Excerpt(body)}";
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey)
            && !string.Equals(parsed.TransactionId, idempotencyKey, StringComparison.Ordinal))
        {
            return $"PaymentsAPI answered idempotency key '{idempotencyKey}' with transactionId '{parsed.TransactionId}'.";
        }

        return MapOutcome(parsed.Status) == PaymentOutcome.TransportFailure
            ? $"PaymentsAPI returned HTTP {(int)statusCode} with the unrecognised, possibly malformed status '{parsed.Status}'."
            : null;
    }

    private static string Excerpt(string body)
    {
        var collapsed = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0)
        {
            return " and an empty body.";
        }

        return collapsed.Length <= BodyExcerptLength
            ? $": {collapsed}"
            : $": {collapsed[..BodyExcerptLength]}…";
    }

    /// <summary>
    /// Maps the acknowledged status word to a business outcome, deliberately
    /// without consulting the HTTP status code. Pairing the two used to reject
    /// legitimate answers as malformed: an instant duplicate whose row is
    /// <c>Completed</c> replays <c>200</c> carrying the persisted delivery
    /// status (<c>PaymentsController.ResolveDeliveredResponse</c>), and that
    /// payload is whatever CoreBankAPI last returned — <c>Pending</c> when the
    /// inline attempt was accepted for deferred execution, or <c>Processing</c>
    /// for an in-flight duplicate. Which HTTP code each rail is allowed to use
    /// is a rail rule, and is enforced where rail rules live
    /// (<c>OperatorConsoleController.EnforceRailSemantics</c>), not here.
    /// </summary>
    private static PaymentOutcome MapOutcome(string? status) => Normalize(status) switch
    {
        "completed" => PaymentOutcome.Completed,
        "failed" => PaymentOutcome.Failed,
        "pending" or "processing" => PaymentOutcome.Pending,
        "cancelled" => PaymentOutcome.Cancelled,
        _ => PaymentOutcome.TransportFailure,
    };

    private static string Normalize(string? status) => status?.Trim().ToLowerInvariant() ?? string.Empty;

    private static PaymentParseResult ParsePaymentResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new PaymentParseResult(null, null, null, true);
            }

            var root = document.RootElement;
            return new PaymentParseResult(
                ReadString(root, "paymentId"),
                ReadString(root, "transactionId"),
                ReadString(root, "status"),
                string.IsNullOrWhiteSpace(ReadString(root, "paymentId"))
                || string.IsNullOrWhiteSpace(ReadString(root, "transactionId"))
                || string.IsNullOrWhiteSpace(ReadString(root, "status")));
        }
        catch (JsonException)
        {
            return new PaymentParseResult(null, null, null, true);
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ToString();
            }
        }

        return null;
    }

    private sealed record PaymentParseResult(
        string? PaymentId,
        string? TransactionId,
        string? Status,
        bool IsMalformed);
}
