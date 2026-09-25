using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// Proves <c>Payments:InstantRail</c> options validation (spec: add-instant-
/// payment-rail; Story 3.1 pattern): defaults are valid, an over-budget
/// attempt configuration fails at startup, and the handler resolves.
/// </summary>
public class InstantPaymentRailRegistrationTests
{
    [Fact]
    public void Default_configuration_registers_a_valid_forwarding_handler()
    {
        using var provider = BuildProvider([]);

        provider.GetRequiredService<IStartupValidator>().Validate();
        var options = provider.GetRequiredService<IOptions<InstantRailOptions>>().Value;
        options.Enabled.Should().BeTrue();
        options.BudgetMilliseconds.Should().Be(9000);
        options.AttemptTimeoutMilliseconds.Should().Be(2500);
        options.MaxAttempts.Should().Be(3);
        options.CancelTimeoutMilliseconds.Should().Be(1500);
        provider.GetRequiredService<IInstantPaymentForwardingHandler>().Should().BeOfType<InstantPaymentForwardingHandler>();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Non_positive_budget_fails_startup_validation(string budgetMilliseconds)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Payments:InstantRail:BudgetMilliseconds"] = budgetMilliseconds
        });

        var act = provider.GetRequiredService<IStartupValidator>().Validate;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void A_single_attempt_that_does_not_fit_beside_the_cancel_allowance_fails_startup_validation()
    {
        // ADR-024: one attempt plus the cancel must fit; 8000 + 1500 > 9000.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Payments:InstantRail:BudgetMilliseconds"] = "9000",
            ["Payments:InstantRail:AttemptTimeoutMilliseconds"] = "8000",
            ["Payments:InstantRail:MaxAttempts"] = "1",
            ["Payments:InstantRail:CancelTimeoutMilliseconds"] = "1500"
        });

        var act = provider.GetRequiredService<IStartupValidator>().Validate;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*AttemptTimeoutMilliseconds + CancelTimeoutMilliseconds must not exceed BudgetMilliseconds*");
    }

    [Fact]
    public void Attempts_that_only_fit_the_budget_one_at_a_time_pass_startup_validation()
    {
        // ADR-024: the loop, not the validator, bounds the number of attempts.
        // 2500 × 4 + 1500 = 11500 > 9000 was rejected under ADR-018/020; a
        // single 2500 + 1500 = 4000 fits, so it is valid now.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Payments:InstantRail:BudgetMilliseconds"] = "9000",
            ["Payments:InstantRail:AttemptTimeoutMilliseconds"] = "2500",
            ["Payments:InstantRail:MaxAttempts"] = "4",
            ["Payments:InstantRail:CancelTimeoutMilliseconds"] = "1500"
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void An_attempt_that_exactly_fits_beside_the_cancel_allowance_passes_startup_validation()
    {
        // 7500 + 1500 = 9000: the boundary itself is valid.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Payments:InstantRail:BudgetMilliseconds"] = "9000",
            ["Payments:InstantRail:AttemptTimeoutMilliseconds"] = "7500",
            ["Payments:InstantRail:MaxAttempts"] = "3",
            ["Payments:InstantRail:CancelTimeoutMilliseconds"] = "1500"
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void A_cancel_allowance_that_pushes_the_attempts_over_budget_fails_startup_validation()
    {
        // AttemptTimeoutMilliseconds + CancelTimeoutMilliseconds must fit
        // inside the budget; 9000 + 1 = 9001 > 9000.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Payments:InstantRail:BudgetMilliseconds"] = "9000",
            ["Payments:InstantRail:AttemptTimeoutMilliseconds"] = "9000",
            ["Payments:InstantRail:MaxAttempts"] = "1",
            ["Payments:InstantRail:CancelTimeoutMilliseconds"] = "1"
        });

        var act = provider.GetRequiredService<IStartupValidator>().Validate;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*CancelTimeoutMilliseconds must not exceed BudgetMilliseconds*");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Non_positive_cancel_timeout_fails_startup_validation(string cancelTimeoutMilliseconds)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Payments:InstantRail:CancelTimeoutMilliseconds"] = cancelTimeoutMilliseconds
        });

        var act = provider.GetRequiredService<IStartupValidator>().Validate;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void A_budget_at_the_full_stale_claim_reclaim_window_fails_startup_validation()
    {
        // Review loop 2: a budget at or above MessageConstants.Defaults.
        // ProcessingTimeout (5 minutes = 300000 ms) would let stale-claim
        // reclaim re-claim a row still legitimately held by an in-flight
        // instant attempt -- reopening a double-execution race on the
        // Payments side. AttemptTimeoutMilliseconds/MaxAttempts are widened
        // too so only the new check can fail.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Payments:InstantRail:BudgetMilliseconds"] = "300000",
            ["Payments:InstantRail:AttemptTimeoutMilliseconds"] = "1000",
            ["Payments:InstantRail:MaxAttempts"] = "2"
        });

        var act = provider.GetRequiredService<IStartupValidator>().Validate;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*stale-claim reclaim window*");
    }

    [Fact]
    public void A_budget_over_half_the_stale_claim_reclaim_window_fails_startup_validation()
    {
        // Half of the 300000 ms reclaim window is 150000 ms -- one
        // millisecond over that must already fail, before the window itself
        // is ever reached.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Payments:InstantRail:BudgetMilliseconds"] = "150001",
            ["Payments:InstantRail:AttemptTimeoutMilliseconds"] = "1000",
            ["Payments:InstantRail:MaxAttempts"] = "2"
        });

        var act = provider.GetRequiredService<IStartupValidator>().Validate;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*stale-claim reclaim window*");
    }

    [Fact]
    public void A_budget_at_exactly_half_the_stale_claim_reclaim_window_passes_startup_validation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Payments:InstantRail:BudgetMilliseconds"] = "150000",
            ["Payments:InstantRail:AttemptTimeoutMilliseconds"] = "1000",
            ["Payments:InstantRail:MaxAttempts"] = "2"
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void Disabled_rail_still_passes_startup_validation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Payments:InstantRail:Enabled"] = "false"
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
        provider.GetRequiredService<IOptions<InstantRailOptions>>().Value.Enabled.Should().BeFalse();
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<BusinessMetrics>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDistributedLockService, NoOpDistributedLockService>();
        services.AddOptions<OutboxProcessingOptions>();
        services.AddInstantPaymentRail(Configuration(values));
        services.AddScoped(_ => new Mock<IOutboxMessageStore<OutboxMessage>>().Object);
        services.AddScoped(_ => new Mock<ICoreBankTransactionForwarder>().Object);
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration(IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
