using System.ComponentModel.DataAnnotations;

namespace CoreBankDemo.PaymentsAPI.Models;

/// <summary>
/// Validated <c>Payments:CoreBankCircuitBreaker</c> options (ADR-029), bound
/// and validated at startup following the Story 3.1 pattern. They configure
/// the one strategy in the <c>corebank-api</c> client's resilience pipeline:
/// a Polly circuit breaker. The ranges mirror Polly's own
/// <c>CircuitBreakerStrategyOptions</c> validation so a bad value fails at
/// startup, naming the field, rather than at the first request.
/// </summary>
public sealed record CoreBankCircuitBreakerOptions
{
    public const string SectionName = "Payments:CoreBankCircuitBreaker";

    /// <summary>
    /// Whether the breaker is in the pipeline at all. When
    /// <see langword="false"/> the <c>corebank-api</c> client registers no
    /// resilience handler, exactly as before ADR-029.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Failures over calls, sampled over <see cref="SamplingDurationSeconds"/>,
    /// at or above which the circuit opens. <c>0.2</c> keeps the Regular Dev
    /// Proxy profile (5 %) and the console's 10 % step closed and lets the
    /// 25 % step open it. Must be greater than zero and at most one.
    /// </summary>
    [Range(0.0, 1.0, ErrorMessage = "FailureRatio must be between 0 (exclusive) and 1 (inclusive).")]
    public double FailureRatio { get; init; } = 0.2;

    /// <summary>
    /// Calls that must fall inside the sampling window before the ratio is
    /// evaluated. Ten per PaymentsAPI replica: a console burst of 50 at
    /// concurrency 10 is enough, one manual request never is.
    /// </summary>
    [Range(2, int.MaxValue, ErrorMessage = "MinimumThroughput must be at least 2.")]
    public int MinimumThroughput { get; init; } = 10;

    /// <summary>The window, in seconds, the failure ratio is computed over.</summary>
    [Range(0.5, 86400.0, ErrorMessage = "SamplingDurationSeconds must be between 0.5 and 86400.")]
    public double SamplingDurationSeconds { get; init; } = 30;

    /// <summary>
    /// How long, in seconds, the open circuit refuses calls before letting
    /// one probe through. Ten seconds: long enough to send a request against
    /// the open circuit on stage, short enough to close while the audience
    /// is still watching.
    /// </summary>
    [Range(0.5, 86400.0, ErrorMessage = "BreakDurationSeconds must be between 0.5 and 86400.")]
    public double BreakDurationSeconds { get; init; } = 10;
}
