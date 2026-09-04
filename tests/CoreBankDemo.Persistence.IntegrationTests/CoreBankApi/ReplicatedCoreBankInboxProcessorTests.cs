using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.CoreBankApi;

public sealed class ReplicatedCoreBankInboxProcessorTests(
    PostgresContainerFixture postgres,
    RedisContainerFixture redis) : CoreBankApiPostgresTestBase(postgres)
{
    [Fact]
    public async Task Same_partition_equal_timestamps_are_exclusive_and_follow_durable_tie_order()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ordered = new[]
        {
            (Guid.Parse("00000000-0000-0000-0000-000000000001"), "first"),
            (Guid.Parse("00000000-0000-0000-0000-000000000002"), "second"),
            (Guid.Parse("00000000-0000-0000-0000-000000000003"), "third")
        };
        await using (var context = CreateContext())
        {
            context.InboxMessages.AddRange(ordered.Select(item => NewMessage(item.Item1, item.Item2)));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var lockFactory = new RedisDistributedLockFactory(multiplexer);
        var attempts = new ConcurrentQueue<string>();
        var bothAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loserFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = new ConcurrentQueue<(string Replica, string Key, long Entered, long Exited)>();
        var handledCount = 0;

        ServiceProvider BuildReplica(string replica) => BuildServices(new RecordingHandler(async message =>
        {
            var entered = Stopwatch.GetTimestamp();
            if (Interlocked.Increment(ref handledCount) == 1)
            {
                await loserFinished.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            handled.Enqueue((replica, message.IdempotencyKey, entered, Stopwatch.GetTimestamp()));
            if (handledCount == ordered.Length)
            {
                allHandled.TrySetResult();
            }
        }));

        using var servicesA = BuildReplica("replica-a");
        using var servicesB = BuildReplica("replica-b");
        var processorA = CreateProcessor(servicesA, new CoordinatedLockService(
            "replica-a",
            new RedisDistributedLockService(lockFactory, NullLogger<RedisDistributedLockService>.Instance),
            attempts, bothAttempted, loserFinished));
        var processorB = CreateProcessor(servicesB, new CoordinatedLockService(
            "replica-b",
            new RedisDistributedLockService(lockFactory, NullLogger<RedisDistributedLockService>.Instance),
            attempts, bothAttempted, loserFinished));

        await processorA.StartAsync(cancellationToken);
        await processorB.StartAsync(cancellationToken);
        await bothAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await allHandled.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await WaitForCompletedAsync(ordered.Length, cancellationToken);
        await processorA.StopAsync(cancellationToken);
        await processorB.StopAsync(cancellationToken);

        attempts.Should().BeEquivalentTo(["replica-a", "replica-b"]);
        handled.Select(item => item.Replica).Distinct().Should().ContainSingle();
        handled.Select(item => item.Key).Should().Equal("first", "second", "third");
        handled.Zip(handled.Skip(1)).Should().OnlyContain(pair => pair.First.Exited <= pair.Second.Entered);
    }

    private ServiceProvider BuildServices(IInboxMessageHandler<InboxMessage> handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(System.TimeProvider.System);
        services.AddSingleton(TestBusinessMetrics.Instance);
        services.AddScoped<CoreBankDbContext>(_ => CreateContext());
        services.AddScoped<InboxMessageRepository>();
        services.AddScoped<IInboxMessageStore<InboxMessage>>(provider => provider.GetRequiredService<InboxMessageRepository>());
        services.AddScoped<IInboxMessageHandler<InboxMessage>>(_ => handler);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static InboxProcessor CreateProcessor(ServiceProvider services, IDistributedLockService lockService) => new(
        lockService,
        services.GetRequiredService<IServiceScopeFactory>(),
        new ActivitySource(nameof(ReplicatedCoreBankInboxProcessorTests)),
        System.TimeProvider.System,
        NullLogger<InboxProcessor>.Instance,
        TestBusinessMetrics.Instance,
        Options.Create(new InboxProcessingOptions
        {
            PartitionCount = 4,
            LockExpirySeconds = 30,
            PollingIntervalMs = 60000
        }));

    private async Task WaitForCompletedAsync(int expectedCount, CancellationToken cancellationToken)
    {
        var deadline = System.TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(10);
        while (System.TimeProvider.System.GetUtcNow() < deadline)
        {
            await using var context = CreateContext();
            if (await context.InboxMessages.CountAsync(
                    message => message.Status == MessageConstants.Status.Completed, cancellationToken) == expectedCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException($"Expected {expectedCount} completed inbox messages.");
    }

    private static InboxMessage NewMessage(Guid id, string key) => new()
    {
        Id = id,
        IdempotencyKey = key,
        TransactionId = key,
        FromAccount = "from",
        ToAccount = "to",
        Amount = 1m,
        Currency = "EUR",
        PartitionId = 0,
        Status = MessageConstants.Status.Pending,
        ReceivedAt = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc)
    };

    private sealed class RecordingHandler(Func<InboxMessage, Task> handle) : IInboxMessageHandler<InboxMessage>
    {
        public Task HandleAsync(InboxMessage message, CancellationToken cancellationToken) => handle(message);
    }

    private sealed class CoordinatedLockService(
        string replica,
        IDistributedLockService inner,
        ConcurrentQueue<string> attempts,
        TaskCompletionSource bothAttempted,
        TaskCompletionSource loserFinished) : IDistributedLockService
    {
        private int _lossReported;

        public async Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            if (lockName != "corebank-inbox-partition-0")
            {
                return await inner.ExecuteWithLockAsync(lockName, lockExpirySeconds, workload, cancellationToken);
            }

            attempts.Enqueue(replica);
            if (attempts.Count == 2)
            {
                bothAttempted.TrySetResult();
            }

            await bothAttempted.Task.WaitAsync(cancellationToken);
            var acquired = await inner.ExecuteWithLockAsync(lockName, lockExpirySeconds, workload, cancellationToken);
            if (!acquired && Interlocked.Exchange(ref _lossReported, 1) == 0)
            {
                loserFinished.TrySetResult();
            }

            return acquired;
        }
    }
}
