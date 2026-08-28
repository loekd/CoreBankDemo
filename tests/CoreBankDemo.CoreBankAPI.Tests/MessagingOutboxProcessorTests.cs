using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CoreBankDemo.CoreBankAPI.Tests;

public class MessagingOutboxProcessorTests : SqliteCoreBankApiTestBase
{
    [Fact]
    public async Task StartAsync_after_successful_publish_completes_the_row()
    {
        var message = await SeedMessageAsync();
        var deliveryStrategy = new Mock<IOutboxDeliveryStrategy<MessagingOutboxMessage>>();
        var lockService = new SingleTickLockService(4);
        await using var storeContext = CreateContext();
        var processor = CreateProcessor(storeContext, lockService, deliveryStrategy.Object);

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await lockService.TickCompleted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        await using var verifyContext = CreateContext();
        var persisted = await verifyContext.MessagingOutboxMessages
            .AsNoTracking()
            .SingleAsync(row => row.Id == message.Id, TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(MessageConstants.Status.Completed);
        persisted.ProcessedAt.Should().Be(TimeProvider.GetUtcNow().UtcDateTime);
        persisted.RetryCount.Should().Be(0);
        deliveryStrategy.Verify(
            strategy => strategy.DeliverAsync(
                It.Is<MessagingOutboxMessage>(row => row.Id == message.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
        lockService.LockNames.Should().BeEquivalentTo(
            "messaging-outbox-partition-0",
            "messaging-outbox-partition-1",
            "messaging-outbox-partition-2",
            "messaging-outbox-partition-3");
    }

    [Fact]
    public async Task StartAsync_when_publish_fails_schedules_retry_through_kernel()
    {
        var message = await SeedMessageAsync();
        var deliveryStrategy = new Mock<IOutboxDeliveryStrategy<MessagingOutboxMessage>>();
        deliveryStrategy
            .Setup(strategy => strategy.DeliverAsync(
                It.IsAny<MessagingOutboxMessage>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("publish failed"));
        var lockService = new SingleTickLockService(4);
        await using var storeContext = CreateContext();
        var processor = CreateProcessor(storeContext, lockService, deliveryStrategy.Object);

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await lockService.TickCompleted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        await using var verifyContext = CreateContext();
        var persisted = await verifyContext.MessagingOutboxMessages
            .AsNoTracking()
            .SingleAsync(row => row.Id == message.Id, TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(MessageConstants.Status.Pending);
        persisted.ProcessedAt.Should().BeNull();
        persisted.RetryCount.Should().Be(1);
        persisted.LastError.Should().Be("publish failed");
    }

    [Fact]
    public void Concrete_processor_overrides_only_lock_name_prefix()
    {
        var declaredMethods = typeof(MessagingOutboxProcessor)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

        declaredMethods.Should().ContainSingle()
            .Which.Name.Should().Be("get_LockNamePrefix");
        declaredMethods[0].GetBaseDefinition().DeclaringType.Should()
            .Be(typeof(OutboxProcessorBase<MessagingOutboxMessage>));
    }

    private MessagingOutboxProcessor CreateProcessor(
        CoreBankDbContext storeContext,
        IDistributedLockService lockService,
        IOutboxDeliveryStrategy<MessagingOutboxMessage> deliveryStrategy) =>
        new(
            new MessagingOutboxRepository(storeContext, TimeProvider),
            lockService,
            deliveryStrategy,
            new ActivitySource(nameof(MessagingOutboxProcessorTests)),
            TimeProvider,
            NullLogger<MessagingOutboxProcessor>.Instance,
            Options.Create(new MessagingOutboxProcessingOptions
            {
                PartitionCount = 4,
                LockExpirySeconds = 30,
                PollingIntervalMs = 60_000
            }));

    private async Task<MessagingOutboxMessage> SeedMessageAsync()
    {
        var message = new MessagingOutboxMessage
        {
            Id = Guid.NewGuid(),
            PartitionId = 2,
            IdempotencyKey = "txn-123",
            Status = MessageConstants.Status.Pending,
            CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
            TraceParent = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01",
            TransactionId = "txn-123",
            EventType = Constants.TransactionCompleted,
            EventSource = "https://corebank-api/transactions",
            AccountNumber = "NL91ABNA0417164300",
            ToAccount = "NL20INGB0001234567",
            Amount = 25m,
            Currency = "EUR",
            TransactionStatus = MessageConstants.Status.Completed
        };

        await using var context = CreateContext();
        context.MessagingOutboxMessages.Add(message);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return message;
    }

    private sealed class SingleTickLockService(int partitionCount) : IDistributedLockService
    {
        private int _completedPartitions;
        private readonly TaskCompletionSource _tickCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentBag<string> LockNames { get; } = [];
        public Task TickCompleted => _tickCompleted.Task;

        public async Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            LockNames.Add(lockName);
            await workload(cancellationToken);
            if (Interlocked.Increment(ref _completedPartitions) == partitionCount)
            {
                _tickCompleted.TrySetResult();
            }

            return true;
        }
    }
}
