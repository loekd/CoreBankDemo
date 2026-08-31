using CoreBankDemo.LoadTestInitializer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults("CoreBank.LoadTestInitializer");
builder.Services.AddHttpClient("loadtest-support", client =>
{
    client.BaseAddress = new Uri("http://loadtest-support");
    client.Timeout = TimeSpan.FromSeconds(45);
}).AddServiceDiscovery();

using var host = builder.Build();
await host.StartAsync();

try
{
    var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("loadtest-support");
    using var response = await client.PostAsync("/reset", content: null);
    var responseBody = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException(
            $"Load-test reset failed with status {(int)response.StatusCode}: {responseBody}");
    }

    ResetResponseValidator.Validate(responseBody);
}
finally
{
    await host.StopAsync();
}
