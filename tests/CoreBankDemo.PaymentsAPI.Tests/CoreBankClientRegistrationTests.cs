using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ServiceDiscovery;
using Microsoft.Kiota.Abstractions;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// Verifies <see cref="CoreBankClientServiceCollectionExtensions.AddCoreBankApiClient"/>
/// wires exactly one resolvable <see cref="ICoreBankApiClient"/> backed by the
/// generated Kiota client, through the named <c>"corebank-api"</c>
/// <see cref="HttpClient"/> resolving <c>http://corebank-api</c> (spec-5-3's
/// code map).
/// </summary>
public class CoreBankClientRegistrationTests
{
    [Fact]
    public void AddCoreBankApiClient_resolves_kiota_backed_client()
    {
        var services = new ServiceCollection();

        services.AddCoreBankApiClient();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var clients = scope.ServiceProvider.GetServices<ICoreBankApiClient>();

        clients.Should().ContainSingle()
            .Which.Should().BeOfType<KiotaCoreBankApiClient>();
    }

    [Fact]
    public void AddCoreBankApiClient_registers_corebank_api_http_client_with_expected_base_address()
    {
        var services = new ServiceCollection();

        services.AddCoreBankApiClient();

        using var provider = services.BuildServiceProvider();
        var httpClient = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(CoreBankClientServiceCollectionExtensions.HttpClientName);

        httpClient.BaseAddress.Should().Be(new Uri("http://corebank-api"));
    }

    [Fact]
    public void AddCoreBankApiClient_resolves_a_scoped_request_adapter_per_scope()
    {
        var services = new ServiceCollection();

        services.AddCoreBankApiClient();

        using var provider = services.BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<IRequestAdapter>();
        var second = secondScope.ServiceProvider.GetRequiredService<IRequestAdapter>();

        first.Should().NotBeSameAs(second);
    }

    /// <summary>
    /// Everything above only checks what's registered/resolvable; this
    /// exercises an actual call through the fully DI-composed pipeline --
    /// <see cref="ICoreBankApiClient"/> resolved from a scope, backed by the
    /// real named <c>"corebank-api"</c> <see cref="HttpClient"/> that
    /// <see cref="CoreBankClientServiceCollectionExtensions.AddCoreBankApiClient"/>
    /// wires up (service discovery included) -- only the transport at the
    /// very bottom (the primary <see cref="HttpMessageHandler"/>) is a stub,
    /// so the whole adapter/Kiota/request-adapter/HttpClient chain in
    /// between is genuinely covered end to end, not just its registrations.
    /// </summary>
    [Fact]
    public async Task AddCoreBankApiClient_call_flows_through_the_composed_http_pipeline()
    {
        const string accountNumber = "NL91ABNA0417164300";
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddServiceDiscovery();
        services.AddCoreBankApiClient();
        services.AddHttpClient(CoreBankClientServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHttpMessageHandler(request =>
            {
                request.RequestUri!.AbsolutePath.Should().Be("/api/accounts/validate");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accountNumber, isValid = true })
                };
            }));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ICoreBankApiClient>();

        var result = await client.ValidateAccountAsync(accountNumber, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Success);
        result.Value.Should().Be(new AccountValidation(accountNumber, true, null, null));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
