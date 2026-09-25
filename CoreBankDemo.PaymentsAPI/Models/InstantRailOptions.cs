using System.ComponentModel.DataAnnotations;

namespace CoreBankDemo.PaymentsAPI.Models;

/// <summary>
/// Validated <c>Payments:InstantRail</c> options (spec: add-instant-payment-
/// rail), bound and validated at startup following the Story 3.1 pattern
/// (<c>AddOptions&lt;T&gt;().Bind(...).ValidateDataAnnotations().Validate(...)
/// .ValidateOnStart()</c>): every value must be positive, and
/// <see cref="AttemptTimeoutMilliseconds"/> plus <see cref="CancelTimeoutMilliseconds"/>
/// must never exceed <see cref="BudgetMilliseconds"/> -- an over-budget
/// configuration fails fast at startup rather than silently holding a
/// request thread beyond its budget at runtime (ADR-024).
/// </summary>
public sealed record InstantRailOptions
{
    public const string SectionName = "Payments:InstantRail";

    /// <summary>
    /// Whether the instant rail is available at all. When
    /// <see langword="false"/>, <c>scheme=instant</c> behaves exactly like
    /// <c>standard</c> (documented, not an error).
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Total wall-clock budget, in milliseconds, a request may spend on the
    /// inline attempt before the caller must be answered. SCT Inst's
    /// scheme-level budget is nine seconds end to end; this is PaymentsAPI's
    /// share of it.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "BudgetMilliseconds must be positive.")]
    public int BudgetMilliseconds { get; init; } = 9000;

    /// <summary>Per-attempt timeout, in milliseconds, for one inline forward attempt.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "AttemptTimeoutMilliseconds must be positive.")]
    public int AttemptTimeoutMilliseconds { get; init; } = 2500;

    /// <summary>
    /// Hard cap on inline attempts. The forward window, not this cap, is
    /// what normally ends the loop (ADR-024): an attempt is started only
    /// while a useful one still fits before <c>Budget - CancelTimeout</c>.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "MaxAttempts must be positive.")]
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Allowance, in milliseconds, reserved <em>inside</em>
    /// <see cref="BudgetMilliseconds"/> for the cancellation call CoreBankAPI
    /// receives once the forward phase ends without a committed outcome (spec:
    /// instant-rail-timeout-cancel). The forward phase ends at
    /// <c>Budget - CancelTimeout</c>, so a request thread is never held beyond
    /// the budget; <c>AttemptTimeoutMilliseconds + CancelTimeoutMilliseconds</c>
    /// must not exceed the budget, so at least one full attempt fits (ADR-024).
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "CancelTimeoutMilliseconds must be positive.")]
    public int CancelTimeoutMilliseconds { get; init; } = 1500;
}
