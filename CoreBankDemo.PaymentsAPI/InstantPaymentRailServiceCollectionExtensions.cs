using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreBankDemo.PaymentsAPI;

/// <summary>
/// Wires the instant-payment-rail options and forwarding handler (spec:
/// add-instant-payment-rail). Follows the Story 3.1 validated-options
/// pattern: DataAnnotations plus cross-field <c>.Validate(...)</c> checks for
/// the budget/attempt-timeout relationship and the budget's margin under the
/// background processor's stale-claim reclaim window,
/// <c>.ValidateOnStart()</c> so a misconfigured budget fails fast rather than
/// silently holding a request thread beyond it -- or racing the reclaim
/// mechanism -- at runtime.
/// </summary>
public static class InstantPaymentRailServiceCollectionExtensions
{
    /// <summary>
    /// The largest fraction of <see cref="MessageConstants.Defaults.ProcessingTimeout"/>
    /// (the background processor's stale-claim reclaim window)
    /// <c>BudgetMilliseconds</c> may occupy. A budget at or above the full
    /// window would let stale-claim reclaim re-claim a row still legitimately
    /// held by an in-flight instant attempt -- reopening, on the Payments
    /// side via a different mechanism, the same class of double-execution
    /// race review loop 1 closed on the CoreBank side (review loop 2).
    /// </summary>
    private const double MaxBudgetFractionOfProcessingTimeout = 0.5;

    public static IServiceCollection AddInstantPaymentRail(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(InstantRailOptions.SectionName);
        var maxBudgetMilliseconds =
            MessageConstants.Defaults.ProcessingTimeout.TotalMilliseconds * MaxBudgetFractionOfProcessingTimeout;
        services.AddOptions<InstantRailOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .Validate(
                options => (long)options.AttemptTimeoutMilliseconds * options.MaxAttempts <= options.BudgetMilliseconds,
                "Payments:InstantRail: AttemptTimeoutMilliseconds * MaxAttempts must not exceed BudgetMilliseconds.")
            .Validate(
                options => options.BudgetMilliseconds <= maxBudgetMilliseconds,
                $"Payments:InstantRail: BudgetMilliseconds must not exceed {maxBudgetMilliseconds} ms " +
                $"(half of the background processor's {MessageConstants.Defaults.ProcessingTimeout.TotalMilliseconds} ms stale-claim reclaim window), " +
                "so a budget-exhausted claim can never still look legitimately in-flight to stale-claim reclaim.")
            .ValidateOnStart();

        services.AddScoped<IInstantPaymentForwardingHandler, InstantPaymentForwardingHandler>();

        return services;
    }
}
