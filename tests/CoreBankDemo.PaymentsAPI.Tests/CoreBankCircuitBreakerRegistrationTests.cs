using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
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

    [Fact]
    public void Default_configuration_puts_exactly_one_resilience_handler_in_the_corebank_api_chain()
    {
        using var provider = BuildProvider([]);
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        var names = WalkDelegatingHandlerChain(factory.CreateHandler(CoreBankClientServiceCollectionExtensions.HttpClientName));

        names.Should().ContainSingle(name => name.Contains("Resilience"),
            "ADR-029 adds one named handler holding the breaker; the standard handler from ConfigureHttpClientDefaults stays removed");
    }

    [Fact]
    public async Task The_standard_handler_is_removed_so_no_retry_happens_underneath_the_breaker()
    {
        // With the standard handler still in the chain, each failed call would
        // be retried three times inside SendAsync (AD-11 forbids that) and its
        // own breaker (MinimumThroughput 100) would never open here.
        var calls = 0;
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"{CoreBankCircuitBreakerOptions.SectionName}:MinimumThroughput"] = "2",
            [$"{CoreBankCircuitBreakerOptions.SectionName}:FailureRatio"] = "0.5",
        }, _ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ICoreBankApiClient>();

        await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);
        await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);

        calls.Should().Be(2, "one request per call: nothing in the pipeline retries");
    }

    [Fact]
    public void Disabled_breaker_registers_no_resilience_handler()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"{CoreBankCircuitBreakerOptions.SectionName}:Enabled"] = "false"
        });
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        var names = WalkDelegatingHandlerChain(factory.CreateHandler(CoreBankClientServiceCollectionExtensions.HttpClientName));

        names.Should().NotContain(name => name.Contains("Resilience"),
            "Enabled=false must restore the pre-ADR-029 chain exactly");
        names.Should().Contain(name => name.Contains(nameof(LastResponseStatusHandler)));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Breaker_opens_after_the_minimum_throughput_of_failures_and_the_client_reports_CircuitOpen(HttpStatusCode failure)
    {
        // MinimumThroughput 2 and ratio 0.5: two failures open the circuit;
        // the third call never reaches the stub. A 429 counts like a 503
        // (Polly's default predicate, kept on purpose; spec Design Notes).
        var calls = 0;
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"{CoreBankCircuitBreakerOptions.SectionName}:MinimumThroughput"] = "2",
            [$"{CoreBankCircuitBreakerOptions.SectionName}:FailureRatio"] = "0.5",
            [$"{CoreBankCircuitBreakerOptions.SectionName}:SamplingDurationSeconds"] = "30",
            [$"{CoreBankCircuitBreakerOptions.SectionName}:BreakDurationSeconds"] = "30",
        }, _ =>
        {
            calls++;
            return new HttpResponseMessage(failure);
        });
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ICoreBankApiClient>();

        var first = await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);
        var second = await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);
        var third = await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);

        first.RetryReason.Should().Be(CoreBankRetryReason.TransportRejection);
        second.RetryReason.Should().Be(CoreBankRetryReason.TransportRejection);
        third.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        third.RetryReason.Should().Be(CoreBankRetryReason.CircuitOpen);
        third.StatusCode.Should().BeNull();
        third.RetryAfter.Should().BeNull();
        calls.Should().Be(2, "an open circuit refuses the call before the transport sees it");
    }

    [Fact]
    public async Task Breaker_half_opens_after_the_break_and_closes_on_a_successful_probe()
    {
        // The shortest break Polly allows is 500 ms; this test really waits
        // it out once. Responses: two failures (opens), then success.
        var calls = 0;
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"{CoreBankCircuitBreakerOptions.SectionName}:MinimumThroughput"] = "2",
            [$"{CoreBankCircuitBreakerOptions.SectionName}:FailureRatio"] = "0.5",
            [$"{CoreBankCircuitBreakerOptions.SectionName}:BreakDurationSeconds"] = "0.5",
        }, _ => ++calls <= 2
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    accountNumber = "NL91ABNA0417164300",
                    accountHolderName = "Jane Doe",
                    balance = 100m,
                    currency = "EUR",
                    isActive = true,
                    createdAt = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero)
                })
            });
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ICoreBankApiClient>();
        await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);
        await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);
        (await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken))
            .RetryReason.Should().Be(CoreBankRetryReason.CircuitOpen);

        await Task.Delay(TimeSpan.FromMilliseconds(700), TestContext.Current.CancellationToken);
        var probe = await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);
        var afterClose = await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);

        probe.Outcome.Should().Be(CoreBankClientOutcome.Success, "the half-open probe went to the wire");
        afterClose.Outcome.Should().Be(CoreBankClientOutcome.Success, "a successful probe closes the circuit");
        calls.Should().Be(4);
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values) =>
        BuildProvider(values, _ => new HttpResponseMessage(HttpStatusCode.OK));

    private static ServiceProvider BuildProvider(
        Dictionary<string, string?> values,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var services = new ServiceCollection();
        var configuration = Configuration(values);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddServiceDiscovery();
        // The real host applies the standard resilience handler to every
        // named client through ConfigureHttpClientDefaults (ServiceDefaults);
        // registering it here proves RemoveAllResilienceHandlers strips it
        // and only the breaker added afterwards survives.
        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
        services.AddCoreBankApiClient(configuration);
        // Only the primary transport is stubbed; the whole composed chain
        // (status capture, breaker, Kiota adapter) runs for real.
        services.AddHttpClient(CoreBankClientServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHttpMessageHandler(respond));
        return services.BuildServiceProvider();
    }

    private static List<string> WalkDelegatingHandlerChain(HttpMessageHandler handler)
    {
        var names = new List<string>();
        var current = handler;
        while (current is DelegatingHandler delegating)
        {
            names.Add(delegating.GetType().FullName ?? delegating.GetType().Name);
            current = delegating.InnerHandler;
            if (current is null)
            {
                break;
            }
        }

        return names;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static IConfiguration Configuration(IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
