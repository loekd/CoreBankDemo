using AwesomeAssertions;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.ServiceDefaults;
using StackExchange.Redis;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.ServiceDefaults;

[Collection("Processor start gate Redis")]
public sealed class ProcessorStartGateIntegrationTests(RedisContainerFixture redis)
{
    [Fact]
    public async Task Redis_broadcast_releases_four_registered_replicas_and_a_late_replica()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var database = multiplexer.GetDatabase();
        await database.KeyDeleteAsync([
            RedisProcessorStartGate.GenerationKey,
            RedisProcessorStartGate.ParticipantsKey,
            "corebankdemo:processor-start:acknowledgements:1"
        ]);

        var gates = Enumerable.Range(0, 4)
            .Select(_ => new RedisProcessorStartGate(
                multiplexer,
                expectedParticipants: 0,
                TimeProvider.System,
                TimeSpan.FromSeconds(10)))
            .ToArray();
        var waits = gates.Select(gate => gate.WaitAsync(cancellationToken)).ToArray();
        waits.Should().OnlyContain(wait => !wait.IsCompleted);

        var publisher = new RedisProcessorStartGate(
            multiplexer,
            expectedParticipants: 4,
            TimeProvider.System,
            TimeSpan.FromSeconds(10));
        await publisher.ReleaseAsync(cancellationToken);
        await Task.WhenAll(waits);

        var generation = (long)await database.StringGetAsync(RedisProcessorStartGate.GenerationKey);
        generation.Should().BeGreaterThan(0);
        (await database.SetLengthAsync($"corebankdemo:processor-start:acknowledgements:{generation}"))
            .Should().Be(4);

        var lateGate = new RedisProcessorStartGate(
            multiplexer,
            expectedParticipants: 0,
            TimeProvider.System,
            TimeSpan.FromSeconds(10));
        await lateGate.WaitAsync(cancellationToken);
        (await database.SetLengthAsync($"corebankdemo:processor-start:acknowledgements:{generation}"))
            .Should().Be(5);
    }
}
