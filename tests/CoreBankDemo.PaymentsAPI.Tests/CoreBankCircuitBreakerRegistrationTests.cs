using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// Proves <c>Payments:CoreBankCircuitBreaker</c> options validation and the
/// breaker's place in the <c>corebank-api</c> handler chain (spec:
/// corebank-circuit-breaker; ADR-029; Story 3.1 options pattern).
/// </summary>
public class CoreBankCircuitBreakerRegistrationTests
{
    [Fact]
    public void Default_configuration_is_valid_and_enabled()
    {
        using var provider = BuildProvider([]);

        provider.GetRequiredService<IStartupValidator>().Validate();
        var options = provider.GetRequiredService<IOptions<CoreBankCircuitBreakerOptions>>().Value;

        options.Enabled.Should().BeTrue();
        options.FailureRatio.Should().Be(0.2);
        options.MinimumThroughput.Should().Be(10);
        options.SamplingDurationSeconds.Should().Be(30);
        options.BreakDurationSeconds.Should().Be(10);
    }

    [Theory]
    [InlineData("FailureRatio", "0", "FailureRatio")]
    [InlineData("FailureRatio", "-0.1", "FailureRatio")]
    [InlineData("FailureRatio", "1.01", "FailureRatio")]
    [InlineData("MinimumThroughput", "1", "MinimumThroughput")]
    [InlineData("SamplingDurationSeconds", "0.4", "SamplingDurationSeconds")]
    [InlineData("SamplingDurationSeconds", "86401", "SamplingDurationSeconds")]
    [InlineData("BreakDurationSeconds", "0.4", "BreakDurationSeconds")]
    [InlineData("BreakDurationSeconds", "86401", "BreakDurationSeconds")]
    public void Out_of_range_values_fail_startup_validation(string key, string value, string namedField)
    {
        // Polly validates the same ranges when the pipeline is first built,
        // at the first request; mirroring them here moves the failure to
        // startup and names the field.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"{CoreBankCircuitBreakerOptions.SectionName}:{key}"] = value
        });

        var act = provider.GetRequiredService<IStartupValidator>().Validate;

        act.Should().Throw<OptionsValidationException>().WithMessage($"*{namedField}*");
    }

    [Theory]
    [InlineData("FailureRatio", "1")]
    [InlineData("FailureRatio", "0.01")]
    [InlineData("MinimumThroughput", "2")]
    [InlineData("SamplingDurationSeconds", "0.5")]
    [InlineData("BreakDurationSeconds", "86400")]
    public void Boundary_values_pass_startup_validation(string key, string value)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"{CoreBankCircuitBreakerOptions.SectionName}:{key}"] = value
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void Disabled_breaker_still_passes_startup_validation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"{CoreBankCircuitBreakerOptions.SectionName}:Enabled"] = "false"
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
        provider.GetRequiredService<IOptions<CoreBankCircuitBreakerOptions>>().Value.Enabled.Should().BeFalse();
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddCoreBankApiClient(Configuration(values));
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration(IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
