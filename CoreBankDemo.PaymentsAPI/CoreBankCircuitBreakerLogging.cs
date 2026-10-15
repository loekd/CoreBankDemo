using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace CoreBankDemo.PaymentsAPI;

/// <summary>
/// Structured log lines for the <c>corebank-api</c> circuit breaker's
/// transitions (ADR-029), so Loki shows when the circuit opened, why, for how
/// long, when it probed and when it closed. Polly's own telemetry carries the
/// same transitions as metrics (meter <c>Polly</c>, instrument
/// <c>resilience.polly.strategy.events</c>); these lines are the narrative.
/// </summary>
internal static class CoreBankCircuitBreakerLogging
{
    /// <summary>Logger category; one place to filter the circuit's life cycle on.</summary>
    internal const string CategoryName = "CoreBankDemo.PaymentsAPI.CoreBankCircuitBreaker";

    /// <summary>
    /// The resilience handler's name. <c>AddResilienceHandler</c> prefixes it
    /// with the client name, so Polly's telemetry reports the pipeline as
    /// <see cref="PipelineName"/>.
    /// </summary>
    internal const string HandlerName = "circuit-breaker";

    /// <summary>
    /// The <c>pipeline.name</c> tag on <c>resilience.polly.strategy.events</c>
    /// (and on Polly's log lines) for this breaker: the client name, a dash,
    /// <see cref="HandlerName"/>. Grafana panels select on this value.
    /// </summary>
    internal const string PipelineName = $"{CoreBankClientServiceCollectionExtensions.HttpClientName}-{HandlerName}";

    /// <summary>Warning: the circuit opened; names the break duration and the status or exception type that tripped it.</summary>
    internal static void Opened(ILogger logger, OnCircuitOpenedArguments<HttpResponseMessage> args)
    {
        var outcome = args.Outcome.Exception is { } exception
            ? exception.GetType().Name
            : ((int?)args.Outcome.Result?.StatusCode)?.ToString() ?? "unknown";
        logger.LogWarning(
            "CoreBank circuit opened for {BreakDurationMs} ms after {TrippingOutcome}; calls to corebank-api fail fast until the break ends (manual: {IsManual})",
            (long)args.BreakDuration.TotalMilliseconds,
            outcome,
            args.IsManual);
    }

    /// <summary>Information: the break elapsed and the next call is the probe.</summary>
    internal static void HalfOpened(ILogger logger, OnCircuitHalfOpenedArguments args) =>
        logger.LogInformation("CoreBank circuit half-open; the next call to corebank-api is the probe");

    /// <summary>Information: the probe succeeded and calls flow again.</summary>
    internal static void Closed(ILogger logger, OnCircuitClosedArguments<HttpResponseMessage> args) =>
        logger.LogInformation("CoreBank circuit closed; calls to corebank-api flow again (manual: {IsManual})", args.IsManual);
}
