using CoreBankDemo.PaymentsAPI.Outbox;
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
        services.AddHttpClient(HttpClientName, client =>
            {
                client.BaseAddress = new Uri($"http://{HttpClientName}");
            })
            .AddHttpMessageHandler(() => new LastResponseStatusHandler())
            .AddServiceDiscovery();

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
