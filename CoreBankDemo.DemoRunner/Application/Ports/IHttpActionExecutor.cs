namespace CoreBankDemo.DemoRunner.Application.Ports;

/// <summary>Outcome of one allow-listed HTTP action against a known local endpoint.</summary>
public sealed record HttpActionResult(bool IsSuccess, int StatusCode, string? BodyJson, string? ErrorSummary, bool IsAmbiguous = false)
{
    public static HttpActionResult Ok(int statusCode, string? bodyJson) => new(true, statusCode, bodyJson, null);
    public static HttpActionResult Error(int statusCode, string summary, string? bodyJson = null) => new(false, statusCode, bodyJson, summary);

    /// <summary>The request may already have reached the server; the outcome is genuinely unproven, not a clean failure.</summary>
    public static HttpActionResult Timeout(string summary) => new(false, 0, null, summary, IsAmbiguous: true);
}

/// <summary>
/// Executes sendHttp/assertHttp actions against the compiled endpoint allow-list
/// (<see cref="Scenarios.KnownEndpoints"/>). Implementations resolve the base URL/path
/// for an endpoint id; scenario data never supplies a URL directly (ADR-015).
/// </summary>
public interface IHttpActionExecutor
{
    Task<HttpActionResult> SendAsync(
        string endpointId,
        string method,
        string? bodyJson,
        string? idempotencyKey,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? query = null);
}
