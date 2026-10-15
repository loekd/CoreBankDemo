using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Polly;
using GeneratedClient = CoreBankDemo.PaymentsAPI.GeneratedClients.CoreBank.CoreBankApiKiotaClient;

namespace CoreBankDemo.PaymentsAPI;

/// <summary>
/// Wires the generated CoreBank Kiota client and its adapter into DI (story
/// 5.3). One logical named HTTP client ("corebank-api") resolves
/// <c>http://corebank-api</c> through Aspire's configured service discovery
/// (<c>ConfigureHttpClientDefaults</c> in <see cref="ServiceDefaults"/>
/// applies the standard resilience handler and service discovery to every
/// other client; this one keeps service discovery and replaces the standard
/// handler with a lone circuit breaker, ADR-029). Every piece is scoped, mirroring how
/// <c>OutboxProcessorBase</c>/<c>InboxProcessorBase</c> resolve their
/// per-message dependencies from a fresh DI scope (messaging-patterns
/// skill) — never a long-lived singleton <see cref="HttpClient"/>.
/// </summary>
internal static class CoreBankClientServiceCollectionExtensions
{
    internal const string HttpClientName = "corebank-api";

    internal static IServiceCollection AddCoreBankApiClient(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // ADR-029: the breaker's thresholds are configuration, validated at
        // startup (Story 3.1 pattern). FailureRatio's lower bound is exclusive
        // (Polly rejects 0), which DataAnnotations' Range cannot express.
        services.AddOptions<CoreBankCircuitBreakerOptions>()
            .Bind(configuration.GetSection(CoreBankCircuitBreakerOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.FailureRatio > 0,
                $"{CoreBankCircuitBreakerOptions.SectionName}: FailureRatio must be greater than 0.")
            .ValidateOnStart();

        // ServiceDefaults' ConfigureHttpClientDefaults applies AddStandardResilienceHandler()
        // (rate limiter, total timeout, retry, circuit breaker, attempt timeout) to every
        // named HttpClient, including this one. AD-11 requires all CoreBank retry decisions to
        // live in the outbox layer (the background kernel, ADR-023; the instant loop, ADR-024),
        // not the HTTP transport, so the standard handler is stripped here and never returns.
        // What this pipeline does hold (ADR-029) is one circuit breaker and nothing else: it
        // sends nothing extra, it only refuses to send while CoreBank is failing, and each
        // refusal is one more failed attempt for the layer above to classify (CircuitOpen).
        // RemoveAllResilienceHandlers is the sanctioned API for opting a single named client
        // out of ConfigureHttpClientDefaults' resilience handler; it is marked experimental
        // (EXTEXP0001) pending API review, not because its behavior is unstable.
        // Enabled is read once here, at registration, because a disabled breaker must leave
        // no handler in the chain; the thresholds are read when the pipeline is first built.
        var breakerEnabled = configuration
            .GetSection(CoreBankCircuitBreakerOptions.SectionName)
            .GetValue(nameof(CoreBankCircuitBreakerOptions.Enabled), defaultValue: true);
#pragma warning disable EXTEXP0001
        var httpClientBuilder = services.AddHttpClient(HttpClientName, client =>
            {
                client.BaseAddress = new Uri($"http://{HttpClientName}");
            })
            .AddHttpMessageHandler(() => new LastResponseStatusHandler())
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        if (breakerEnabled)
        {
            httpClientBuilder.AddResilienceHandler(CoreBankCircuitBreakerLogging.HandlerName, (pipeline, context) =>
            {
                var options = context.ServiceProvider.GetRequiredService<IOptions<CoreBankCircuitBreakerOptions>>().Value;
                var logger = context.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(CoreBankCircuitBreakerLogging.CategoryName);
                // ShouldHandle is left at Polly's HTTP default on purpose: 5xx, 408, 429,
                // HttpRequestException and TimeoutRejectedException count as failures; a
                // caller-cancelled attempt (the instant rail's per-attempt timer) does not.
                // The breaker keeps time on the TimeProvider registered in DI (Polly's
                // registry resolves it), the same clock the instant rail budgets on.
                pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = options.FailureRatio,
                    MinimumThroughput = options.MinimumThroughput,
                    SamplingDuration = TimeSpan.FromSeconds(options.SamplingDurationSeconds),
                    BreakDuration = TimeSpan.FromSeconds(options.BreakDurationSeconds),
                    OnOpened = args =>
                    {
                        CoreBankCircuitBreakerLogging.Opened(logger, args);
                        return default;
                    },
                    OnHalfOpened = args =>
                    {
                        CoreBankCircuitBreakerLogging.HalfOpened(logger, args);
                        return default;
                    },
                    OnClosed = args =>
                    {
                        CoreBankCircuitBreakerLogging.Closed(logger, args);
                        return default;
                    },
                });
            });
        }

        httpClientBuilder.AddServiceDiscovery();

        // The client reads an HTTP-date Retry-After on the same clock the
        // instant rail budgets on (ADR-024); ServiceDefaults registers the
        // system clock too, so this only matters for a bare registration.
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IRequestAdapter>(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            return new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: httpClient);
        });

        services.AddScoped(sp => new GeneratedClient(sp.GetRequiredService<IRequestAdapter>()));
        services.AddScoped<ICoreBankApiClient, KiotaCoreBankApiClient>();

        return services;
    }
}
