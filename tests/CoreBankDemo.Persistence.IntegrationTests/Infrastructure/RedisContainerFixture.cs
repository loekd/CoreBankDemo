using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Infrastructure;

public sealed class RedisContainerFixture : IAsyncLifetime
{
    private const ushort RedisPort = 6379;
    private readonly IContainer _container = new ContainerBuilder("redis:7.4-alpine")
        .WithPortBinding(RedisPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(RedisPort))
        .Build();

    public string ConnectionString =>
        $"{_container.Hostname}:{_container.GetMappedPublicPort(RedisPort)},abortConnect=false";

    public async ValueTask InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(PostgresContainerFixture.StartupTimeout);
        await _container.StartAsync(timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
