using CoreBankDemo.PaymentsAPI.Outbox;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using GeneratedClient = CoreBankDemo.PaymentsAPI.GeneratedClients.CoreBank.CoreBankApiKiotaClient;

namespace CoreBankDemo.PaymentsAPI;

/// <summary>
/// Wires the generated CoreBank Kiota client and its adapter into DI (story
/// 5.3). One logical named HTTP client ("corebank-api") resolves
/// <c>http://corebank-api</c> through Aspire's configured service discovery
/// (<c>ConfigureHttpClientDefaults</c> in <see cref="ServiceDefaults"/>
/// already applies resilience/service-discovery to every client the factory
/// creates). Every piece is scoped, mirroring how
/// <c>OutboxProcessorBase</c>/<c>InboxProcessorBase</c> resolve their
/// per-message dependencies from a fresh DI scope (messaging-patterns
/// skill) — never a long-lived singleton <see cref="HttpClient"/>.
/// </summary>
internal static class CoreBankClientServiceCollectionExtensions
{
    internal const string HttpClientName = "corebank-api";

    internal static IServiceCollection AddCoreBankApiClient(this IServiceCollection services)
    {
        // ServiceDefaults' ConfigureHttpClientDefaults applies AddStandardResilienceHandler()
        // (with its own retry policy) to every named HttpClient, including this one. AD-11
        // requires all CoreBank retry decisions to live in the outbox layer, not the HTTP
        // transport, so strip the resilience handler here to avoid double-retrying underneath
        // KiotaCoreBankApiClient.ExecuteAsync's own outcome classification.
        // RemoveAllResilienceHandlers is the sanctioned API for opting a single named client
        // out of ConfigureHttpClientDefaults' resilience handler; it is marked experimental
        // (EXTEXP0001) pending API review, not because its behavior is unstable.
#pragma warning disable EXTEXP0001
        services.AddHttpClient(HttpClientName, client =>
            {
                client.BaseAddress = new Uri($"http://{HttpClientName}");
            })
            .AddHttpMessageHandler(() => new LastResponseStatusHandler())
            .RemoveAllResilienceHandlers()
            .AddServiceDiscovery();
#pragma warning restore EXTEXP0001

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
