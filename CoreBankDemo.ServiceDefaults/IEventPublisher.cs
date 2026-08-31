namespace CoreBankDemo.ServiceDefaults;

/// <summary>
/// Port for publishing a CloudEvent-shaped domain event (AD-6: infrastructure
/// reached only through this port, <see cref="Dapr.Client.DaprClient"/>,
/// never elsewhere). Pub/sub component and topic names are not parameters
/// here — an implementation binds them via DI-supplied options instead, so
/// callers stay call-site-simple.
/// </summary>
/// <remarks>
/// ADR-017 extends the original story 3.3 port with W3C tracestate so the
/// complete persisted trace context crosses the Dapr pub/sub hop.
/// </remarks>
public interface IEventPublisher
{
    /// <summary>
    /// Publishes <paramref name="payload"/> as a CloudEvent of type
    /// <paramref name="type"/> from <paramref name="source"/>, tagged with
    /// <paramref name="subject"/>.
    /// </summary>
    /// <param name="type">CloudEvent <c>type</c> — one of the <see cref="CloudEventTypes.Constants"/> values.</param>
    /// <param name="source">CloudEvent <c>source</c> — the publishing service's identity.</param>
    /// <param name="subject">CloudEvent <c>subject</c> — typically the transaction id.</param>
    /// <param name="payload">The event body to publish.</param>
    /// <param name="traceParent">
    /// W3C traceparent to propagate on the Dapr hop (AD-8), or <c>null</c>/
    /// whitespace to omit trace propagation entirely — an implementation must
    /// not send an empty-string traceparent.
    /// </param>
    /// <param name="traceState">W3C tracestate to propagate, or null/whitespace to omit.</param>
    /// <param name="cancellationToken">Ambient cancellation token.</param>
    Task PublishAsync(
        string type,
        string source,
        string subject,
        object payload,
        string? traceParent,
        string? traceState,
        CancellationToken cancellationToken = default);
}
