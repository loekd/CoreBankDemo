using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.CoreBankAPI.Outbox;
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

public sealed class ReplicatedCoreBankOutboxProcessorTests(
    PostgresContainerFixture postgres,
    RedisContainerFixture redis) : CoreBankApiPostgresTestBase(postgres)
{
    [Fact]
    public async Task Same_partition_equal_timestamps_contend_and_follow_durable_tie_order()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ordered = new[]
        {
            (Guid.Parse("00000000-0000-0000-0000-000000000001"), "first"),
            (Guid.Parse("00000000-0000-0000-0000-000000000002"), "second"),
            (Guid.Parse("00000000-0000-0000-0000-000000000003"), "third")
        };
        await SeedAsync(ordered.Select(item => NewMessage(item.Item1, item.Item2, partitionId: 0)));

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var lockFactory = new RedisDistributedLockFactory(multiplexer);
        var attempts = new ConcurrentQueue<string>();
        var bothAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loserFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveriesCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = new ConcurrentQueue<(string Instance, string Key)>();
        var deliveryCount = 0;

        ServiceProvider BuildReplica(string instance) => BuildServices(new RecordingStrategy(async message =>
        {
            deliveries.Enqueue((instance, message.IdempotencyKey));
            var completed = Interlocked.Increment(ref deliveryCount);
            if (completed == 1)
            {
                await loserFinished.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            if (completed == ordered.Length)
            {
                deliveriesCompleted.TrySetResult();
            }
        }));

        using var servicesA = BuildReplica("replica-a");
        using var servicesB = BuildReplica("replica-b");
        var processorA = CreateProcessor(
            servicesA,
            new CoordinatedLockService(
                "replica-a",
                new RedisDistributedLockService(lockFactory, NullLogger<RedisDistributedLockService>.Instance),
                attempts,
                bothAttempted,
                loserFinished),
            partitionCount: 4);
        var processorB = CreateProcessor(
            servicesB,
            new CoordinatedLockService(
                "replica-b",
                new RedisDistributedLockService(lockFactory, NullLogger<RedisDistributedLockService>.Instance),
                attempts,
                bothAttempted,
                loserFinished),
            partitionCount: 4);

        await processorA.StartAsync(cancellationToken);
        await processorB.StartAsync(cancellationToken);
        await bothAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await deliveriesCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await WaitForCompletedAsync(expectedCount: 3, cancellationToken);
        await processorA.StopAsync(cancellationToken);
        await processorB.StopAsync(cancellationToken);

        attempts.Should().BeEquivalentTo(["replica-a", "replica-b"]);
        deliveries.Select(delivery => delivery.Instance).Distinct().Should().ContainSingle();
        deliveries.Select(delivery => delivery.Key).Should().Equal("first", "second", "third");
    }

    [Fact]
    public async Task Different_partitions_overlap_on_different_replica_identities()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync([
            NewMessage(Guid.NewGuid(), "partition-zero", partitionId: 0),
            NewMessage(Guid.NewGuid(), "partition-one", partitionId: 1)
        ]);

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var lockFactory = new RedisDistributedLockFactory(multiplexer);
        var prelockService = new RedisDistributedLockService(
            lockFactory, NullLogger<RedisDistributedLockService>.Instance);
        var prelockHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrelock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prelock = prelockService.ExecuteWithLockAsync(
            "messaging-outbox-partition-1",
            30,
            async ct =>
            {
                prelockHeld.TrySetResult();
                await releasePrelock.Task.WaitAsync(ct);
            },
            cancellationToken);
        await prelockHeld.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        var records = new ConcurrentQueue<ProcessingInterval>();
        var replicaAStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replicaASkippedPartitionOne = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replicaBStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDeliveries = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var servicesA = BuildBlockingServices(
            "replica-a", records, replicaAStarted, releaseDeliveries);
        using var servicesB = BuildBlockingServices(
            "replica-b", records, replicaBStarted, releaseDeliveries);
        var processorA = CreateProcessor(
            servicesA,
            new ObservingLockService(
                new RedisDistributedLockService(lockFactory, NullLogger<RedisDistributedLockService>.Instance),
                "messaging-outbox-partition-1",
                replicaASkippedPartitionOne),
            partitionCount: 4);
        var processorB = CreateProcessor(
            servicesB,
            new RedisDistributedLockService(lockFactory, NullLogger<RedisDistributedLockService>.Instance),
            partitionCount: 4);

        await processorA.StartAsync(cancellationToken);
        await replicaAStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await replicaASkippedPartitionOne.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        releasePrelock.TrySetResult();
        (await prelock).Should().BeTrue();

        await processorB.StartAsync(cancellationToken);
        await replicaBStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        releaseDeliveries.TrySetResult();
        await WaitForCompletedAsync(expectedCount: 2, cancellationToken);
        await processorA.StopAsync(cancellationToken);
        await processorB.StopAsync(cancellationToken);

        records.Should().HaveCount(2);
        var replicaA = records.Single(record => record.Instance == "replica-a");
        var replicaB = records.Single(record => record.Instance == "replica-b");
        replicaA.Key.Should().Be("partition-zero");
        replicaB.Key.Should().Be("partition-one");
        replicaA.Entered.Should().BeLessThan(replicaB.Exited);
        replicaB.Entered.Should().BeLessThan(replicaA.Exited);
    }

    private ServiceProvider BuildBlockingServices(
        string instance,
        ConcurrentQueue<ProcessingInterval> records,
        TaskCompletionSource started,
        TaskCompletionSource release) =>
        BuildServices(new RecordingStrategy(async message =>
        {
            var entered = Stopwatch.GetTimestamp();
            started.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            records.Enqueue(new ProcessingInterval(
                instance,
                message.IdempotencyKey,
                entered,
                Stopwatch.GetTimestamp()));
        }));

    private ServiceProvider BuildServices(IOutboxDeliveryStrategy<MessagingOutboxMessage> strategy)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(System.TimeProvider.System);
        services.AddSingleton(TestBusinessMetrics.Instance);
        services.AddScoped<CoreBankDbContext>(_ => CreateContext());
        services.AddScoped<MessagingOutboxRepository>();
        services.AddScoped<IOutboxMessageStore<MessagingOutboxMessage>>(
            provider => provider.GetRequiredService<MessagingOutboxRepository>());
        services.AddScoped<IOutboxDeliveryStrategy<MessagingOutboxMessage>>(_ => strategy);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static MessagingOutboxProcessor CreateProcessor(
        ServiceProvider services,
        IDistributedLockService lockService,
        int partitionCount) =>
        new(
            lockService,
            services.GetRequiredService<IServiceScopeFactory>(),
            new ActivitySource(nameof(ReplicatedCoreBankOutboxProcessorTests)),
            System.TimeProvider.System,
            NullLogger<MessagingOutboxProcessor>.Instance,
            TestBusinessMetrics.Instance,
            Options.Create(new MessagingOutboxProcessingOptions
            {
                PartitionCount = partitionCount,
                LockExpirySeconds = 30,
                PollingIntervalMs = 60000
            }));

    private async Task SeedAsync(IEnumerable<MessagingOutboxMessage> messages)
    {
        await using var context = CreateContext();
        context.MessagingOutboxMessages.AddRange(messages);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task WaitForCompletedAsync(int expectedCount, CancellationToken cancellationToken)
    {
        var deadline = System.TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(10);
        while (System.TimeProvider.System.GetUtcNow() < deadline)
        {
            await using var context = CreateContext();
            if (await context.MessagingOutboxMessages.CountAsync(
                    message => message.Status == MessageConstants.Status.Completed,
                    cancellationToken) == expectedCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException($"Expected {expectedCount} completed messaging outbox messages.");
    }

    private static MessagingOutboxMessage NewMessage(Guid id, string key, int partitionId) => new()
    {
        Id = id,
        PartitionId = partitionId,
        IdempotencyKey = key,
        Status = MessageConstants.Status.Pending,
        CreatedAt = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc),
        EventOccurredAt = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc),
        TransactionId = key,
        EventType = "test.event",
        EventSource = "https://corebank-api/tests",
        AccountNumber = $"account-{key}",
        ToAccount = "target-account",
        Amount = 1m,
        Currency = "EUR",
        TransactionStatus = MessageConstants.Status.Completed
    };

    private sealed class RecordingStrategy(Func<MessagingOutboxMessage, Task> deliver)
        : IOutboxDeliveryStrategy<MessagingOutboxMessage>
    {
        public Task DeliverAsync(
            MessagingOutboxMessage message,
            CancellationToken cancellationToken = default) => deliver(message);
    }

    private sealed class CoordinatedLockService(
        string instance,
        IDistributedLockService inner,
        ConcurrentQueue<string> attempts,
        TaskCompletionSource bothAttempted,
        TaskCompletionSource loserFinished) : IDistributedLockService
    {
        private int _attemptCount;

        public async Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            if (lockName != "messaging-outbox-partition-0")
            {
                return await inner.ExecuteWithLockAsync(
                    lockName, lockExpirySeconds, workload, cancellationToken);
            }

            attempts.Enqueue(instance);
            if (attempts.Count == 2)
            {
                bothAttempted.TrySetResult();
            }

            await bothAttempted.Task.WaitAsync(cancellationToken);
            var acquired = await inner.ExecuteWithLockAsync(
                lockName, lockExpirySeconds, workload, cancellationToken);
            if (!acquired && Interlocked.Exchange(ref _attemptCount, 1) == 0)
            {
                loserFinished.TrySetResult();
            }

            return acquired;
        }
    }

    private sealed class ObservingLockService(
        IDistributedLockService inner,
        string observedLockName,
        TaskCompletionSource completed) : IDistributedLockService
    {
        public async Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            var acquired = await inner.ExecuteWithLockAsync(
                lockName, lockExpirySeconds, workload, cancellationToken);
            if (lockName == observedLockName)
            {
                completed.TrySetResult();
            }

            return acquired;
        }
    }

    private sealed record ProcessingInterval(string Instance, string Key, long Entered, long Exited);
}
