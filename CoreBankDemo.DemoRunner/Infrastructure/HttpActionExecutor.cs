using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>Executes sendHttp/assertHttp actions against the compiled endpoint allow-list using a real <see cref="HttpClient"/>.</summary>
public sealed class HttpActionExecutor(HttpClient httpClient) : IHttpActionExecutor
{
    public async Task<HttpActionResult> SendAsync(
        string endpointId,
        string method,
        string? bodyJson,
        string? idempotencyKey,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? query = null)
    {
        var (url, defaultMethod) = EndpointResolver.EndpointFor(endpointId);
        if (query is { Count: > 0 })
        {
            url += "?" + string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        }

        var httpMethod = string.IsNullOrWhiteSpace(method) ? defaultMethod : new HttpMethod(method);

        using var request = new HttpRequestMessage(httpMethod, url);
        if (bodyJson is not null)
        {
            request.Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json");
        }

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        try
        {
            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return response.IsSuccessStatusCode
                ? HttpActionResult.Ok((int)response.StatusCode, body)
                : HttpActionResult.Error((int)response.StatusCode, $"HTTP {(int)response.StatusCode}", body);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // The request may have already reached the server — genuinely ambiguous,
            // never a clean failure. Retry must reconcile via a read/assert action.
            return HttpActionResult.Timeout($"Request to {endpointId} timed out.");
        }
        catch (HttpRequestException ex)
        {
            // The connection itself failed (e.g. service not running); nothing was
            // sent, so this is a clean, non-ambiguous failure.
            return HttpActionResult.Error(0, $"Could not reach {endpointId}: {ex.Message}");
        }
    }
}
