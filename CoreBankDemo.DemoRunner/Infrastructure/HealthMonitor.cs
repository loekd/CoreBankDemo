using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>Probes a known resource's health endpoint over HTTP.</summary>
public sealed class HealthMonitor(HttpClient httpClient, TimeProvider time) : IHealthMonitor
{
    public async Task<HealthStatus> CheckAsync(string resourceName, CancellationToken ct)
    {
        return await CheckAsync(resourceName, TopologyProfile.Regular, ct);
    }

    public async Task<HealthStatus> CheckAsync(string resourceName, TopologyProfile profile, CancellationToken ct)
    {
        try
        {
            var url = EndpointResolver.HealthUrlFor(resourceName, profile);
            using var response = await httpClient.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? HealthStatus.Healthy : HealthStatus.Unhealthy;
        }
        catch (HttpRequestException)
        {
            return HealthStatus.Unreachable;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return HealthStatus.Unreachable;
        }
    }

    public async Task<bool> WaitForHealthyAsync(string resourceName, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + timeout;
        while (time.GetUtcNow() < deadline)
        {
            if (await CheckAsync(resourceName, ct) == HealthStatus.Healthy)
            {
                return true;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
        }

        return false;
    }
}
