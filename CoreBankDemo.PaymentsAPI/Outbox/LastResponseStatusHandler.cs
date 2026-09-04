namespace CoreBankDemo.PaymentsAPI.Outbox;

/// <summary>
/// Delegating handler inserted into the "corebank-api" named
/// <see cref="HttpClient"/>'s pipeline (story 5.3 patch). Kiota's own
/// non-2xx handling normally surfaces a <c>Microsoft.Kiota.Abstractions.
/// ApiException</c> that already carries the HTTP status code -- but when
/// the mapped error body itself is malformed, Kiota's deserialization can
/// throw before it ever constructs that exception, and the status code
/// would otherwise be lost to a generic transport-exception classification.
/// Capturing only the numeric status code here -- never headers or the
/// response body -- lets <see cref="KiotaCoreBankApiClient"/> still preserve
/// it in that edge case (frozen matrix: "preserve status/diagnostic context
/// without generated types").
///
/// <para>
/// <see cref="BeginCapture"/> hands the caller a plain mutable
/// <see cref="StatusCapture"/> instance to read directly afterwards, rather
/// than having the caller re-read an <see cref="AsyncLocal{T}"/> once the
/// call completes: the underlying <see cref="HttpClient"/>/transport
/// pipeline does not guarantee that an <see cref="AsyncLocal{T}"/> mutation
/// made deep inside its own internal continuations flows back out to the
/// method that originally awaited it (only that an ambient value already
/// set flows *forward* into what it calls) -- reading the same object
/// reference directly sidesteps that entirely.
/// </para>
/// </summary>
internal sealed class LastResponseStatusHandler : DelegatingHandler
{
    private static readonly AsyncLocal<StatusCapture?> Ambient = new();

    /// <summary>
    /// Starts a fresh capture slot for the current logical call. Must be
    /// called before the request is sent; the returned instance is what the
    /// caller should read afterwards.
    /// </summary>
    internal static StatusCapture BeginCapture()
    {
        var capture = new StatusCapture();
        Ambient.Value = capture;
        return capture;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var capture = Ambient.Value;
        if (capture is not null)
            capture.StatusCode = (int)response.StatusCode;
        return response;
    }

    /// <summary>Mutable holder for the status code observed on one call.</summary>
    internal sealed class StatusCapture
    {
        internal int? StatusCode { get; set; }
    }
}
