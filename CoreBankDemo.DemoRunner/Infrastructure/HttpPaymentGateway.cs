using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

public sealed class HttpPaymentGateway(HttpClient httpClient) : IPaymentGateway
{
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

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();
            var parsed = ParsePaymentResponse(body);
            var outcome = response.StatusCode switch
            {
                HttpStatusCode.Accepted when string.Equals(parsed.Status, "Failed", StringComparison.OrdinalIgnoreCase) => PaymentOutcome.Failed,
                HttpStatusCode.Accepted => PaymentOutcome.Pending,
                HttpStatusCode.OK when string.Equals(parsed.Status, "Completed", StringComparison.OrdinalIgnoreCase) => PaymentOutcome.Completed,
                HttpStatusCode.OK when string.Equals(parsed.Status, "Failed", StringComparison.OrdinalIgnoreCase) => PaymentOutcome.Failed,
                _ when response.IsSuccessStatusCode => PaymentOutcome.TransportFailure,
                _ => PaymentOutcome.TransportFailure,
            };
            if (response.IsSuccessStatusCode
                && (parsed.IsMalformed
                    || (!string.IsNullOrWhiteSpace(submission.IdempotencyKey)
                        && !string.Equals(parsed.TransactionId, submission.IdempotencyKey, StringComparison.Ordinal))))
            {
                outcome = PaymentOutcome.TransportFailure;
            }
            return new PaymentResult(
                outcome,
                (int)response.StatusCode,
                parsed.PaymentId,
                parsed.TransactionId,
                parsed.Status,
                body,
                outcome == PaymentOutcome.TransportFailure
                    ? "PaymentsAPI returned a malformed or mismatched response."
                    : response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}",
                stopwatch.Elapsed);
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
                stopwatch.Elapsed);
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
                stopwatch.Elapsed);
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

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await httpClient.SendAsync(new HttpRequestMessage(method, url), ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();
            return new InspectionResult(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                endpointId,
                body,
                response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}",
                stopwatch.Elapsed);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new InspectionResult(false, 0, endpointId, null, "Request timed out.", stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return new InspectionResult(false, 0, endpointId, null, ex.Message, stopwatch.Elapsed);
        }
    }

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
