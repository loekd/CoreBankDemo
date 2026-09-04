using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

/// <summary>
/// Mirrors <c>CoreBankDemo.CoreBankAPI.Tests.MessagingOutboxProcessorTests</c>'
/// shape (spec-5-4's code map): publish+complete, the kernel retry
/// transition, and the concrete processor's lock-name-prefix-only override,
/// via the real <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
/// <c>StartAsync</c>/<c>StopAsync</c> lifecycle against a PostgreSQL-backed
/// <see cref="OutboxRepository"/> -- <see cref="OutboxProcessorBase{TMessage}"/>'s
/// own poll/lock/claim/retry machinery is exercised at the kernel level by
/// <c>CoreBankDemo.Messaging.Tests.OutboxProcessorBaseTests</c>, so this file
/// only needs to prove <see cref="PaymentsOutboxProcessor"/> and
/// <see cref="HttpForwardOutboxDeliveryStrategy"/> compose correctly, plus
/// the interleaving/ordering proof across two concurrently-progressing
/// partitions.
/// </summary>
public class PaymentsOutboxProcessorTests(PostgresContainerFixture fixture) : PaymentsPostgresTestBase(fixture)
{
    [Fact]
    public async Task StartAsync_delivers_and_completes_a_claimed_row()
    {
        await using var store = CreateStore();
        await SeedAsync(store, "payment-1", partitionId: 0);
        var client = new RecordingCoreBankApiClient();
        using var services = BuildServices(store, client);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockService = new SingleTickLockService(completion);
        var processor = CreateProcessor(
            services.GetRequiredService<IServiceScopeFactory>(), lockService, lockExpirySeconds: 17);

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        await using var verifyContext = store.CreateContext();
        var row = await verifyContext.OutboxMessages
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        row.Status.Should().Be(MessageConstants.Status.Completed);
        row.RetryCount.Should().Be(0);
        lockService.LockName.Should().Be("payments-outbox-partition-0");
        lockService.LockExpirySeconds.Should().Be(17);
        client.SubmittedTransactionIds.Should().Equal("payment-1");
    }

    [Fact]
    public async Task StartAsync_when_destination_account_is_invalid_applies_the_kernel_retry_transition()
    {
        await using var store = CreateStore();
        await SeedAsync(store, "payment-2", partitionId: 0);
        var client = new InvalidAccountCoreBankApiClient();
        using var services = BuildServices(store, client);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = CreateProcessor(
            services.GetRequiredService<IServiceScopeFactory>(), new SingleTickLockService(completion));

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        await using var verifyContext = store.CreateContext();
        var row = await verifyContext.OutboxMessages
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        row.Status.Should().Be(MessageConstants.Status.Pending);
        row.RetryCount.Should().Be(1);
        row.LastError.Should().NotBeNullOrWhiteSpace();
        client.SubmitAttempted.Should().BeFalse();
    }

    [Fact]
    public void Concrete_processor_overrides_only_the_lock_name_prefix_and_store_name()
    {
        var declaredMethods = typeof(PaymentsOutboxProcessor)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        // Story 6.5 adds a second override (StoreName) alongside the
        // existing LockNamePrefix — everything else (polling, partition
        // fan-out, locking, claiming, retry/terminal-failure classification)
        // still stays owned by the base class.
        declaredMethods.Select(m => m.Name).Should().BeEquivalentTo(["get_LockNamePrefix", "get_StoreName"]);
    }

    [Fact]
    public async Task StartAsync_uses_the_configured_polling_interval()
    {
        await using var store = CreateStore();
        using var services = BuildServices(store, new RecordingCoreBankApiClient());
        var secondTick = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockService = new PollingIntervalLockService(secondTick);
        var processor = CreateProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            lockService,
            pollingIntervalMs: 100);

        await processor.StartAsync(TestContext.Current.CancellationToken);
        var elapsedBetweenTicks = await secondTick.Task
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        elapsedBetweenTicks.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(75));
        elapsedBetweenTicks.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAsync_during_delivery_leaves_the_claimed_row_processing_without_a_retry(
        bool cancelDuringSubmission)
    {
        await using var store = CreateStore();
        await SeedAsync(store, "payment-cancelled", partitionId: 0);
        var client = new CancellationBlockingCoreBankApiClient(cancelDuringSubmission);
        using var services = BuildServices(store, client);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = CreateProcessor(
            services.GetRequiredService<IServiceScopeFactory>(), new SingleTickLockService(completion));

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await client.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        await using var verifyContext = store.CreateContext();
        var row = await verifyContext.OutboxMessages
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        row.Status.Should().Be(MessageConstants.Status.Processing);
        row.RetryCount.Should().Be(0);
        row.LastError.Should().BeNull();
    }

    /// <summary>
    /// Interleaving/ordering proof (acceptance criteria): two partitions with
    /// claimed messages progress independently on the same tick while each
    /// partition's own delivery order stays oldest-first.
    /// </summary>
    [Fact]
    public async Task StartAsync_delivers_two_partitions_concurrently_without_cross_partition_reordering()
    {
        await using var store = CreateStore();
        await SeedOrderedAsync(store, partitionId: 0, keys: new[] { "p0-a", "p0-b" });
        await SeedOrderedAsync(store, partitionId: 1, keys: new[] { "p1-a", "p1-b" });
        var client = new ConcurrentRecordingCoreBankApiClient();
        using var services = BuildServices(store, client);
        var partition0Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var partition1Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allPartitionsSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockService = new TwoPartitionLockService(partition0Done, partition1Done, allPartitionsSeen);
        var processor = CreateProcessor(
            services.GetRequiredService<IServiceScopeFactory>(), lockService, partitionCount: 4);

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(partition0Done.Task, partition1Done.Task, allPartitionsSeen.Task)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        client.SubmittedTransactionIds
            .Where(id => id.StartsWith("p0-", StringComparison.Ordinal))
            .Should().Equal("p0-a", "p0-b");
        client.SubmittedTransactionIds
            .Where(id => id.StartsWith("p1-", StringComparison.Ordinal))
            .Should().Equal("p1-a", "p1-b");
        lockService.LockNames.Should().Contain("payments-outbox-partition-0");
        lockService.LockNames.Should().Contain("payments-outbox-partition-1");
        lockService.LockNames.Should().Contain("payments-outbox-partition-2");
        lockService.LockNames.Should().Contain("payments-outbox-partition-3");
    }

    private static ServiceProvider BuildServices(PaymentsStore store, ICoreBankApiClient client)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(System.TimeProvider.System);
        services.AddSingleton(TestBusinessMetrics.Instance);
        services.AddScoped<PaymentsDbContext>(_ => store.CreateContext());
        services.AddScoped<OutboxRepository>();
        services.AddScoped<IOutboxMessageStore<OutboxMessage>>(
            sp => sp.GetRequiredService<OutboxRepository>());
        services.AddScoped<IOutboxDeliveryStrategy<OutboxMessage>>(
            _ => new HttpForwardOutboxDeliveryStrategy(client, TestBusinessMetrics.Instance));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static PaymentsOutboxProcessor CreateProcessor(
        IServiceScopeFactory scopeFactory,
        IDistributedLockService lockService,
        int partitionCount = 1,
        int pollingIntervalMs = 60000,
        int lockExpirySeconds = 30) =>
        new(
            lockService,
            scopeFactory,
            new ActivitySource(nameof(PaymentsOutboxProcessorTests)),
            System.TimeProvider.System,
            NullLogger<PaymentsOutboxProcessor>.Instance,
            TestBusinessMetrics.Instance,
            Options.Create(new OutboxProcessingOptions
            {
                PartitionCount = partitionCount,
                LockExpirySeconds = lockExpirySeconds,
                PollingIntervalMs = pollingIntervalMs
            }));

    private static async Task SeedAsync(PaymentsStore store, string key, int partitionId)
    {
        await using var context = store.CreateContext();
        var message = PaymentsApiTestData.Outbox(key);
        message.PartitionId = partitionId;
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task SeedOrderedAsync(
        PaymentsStore store, int partitionId, IReadOnlyList<string> keys)
    {
        await using var context = store.CreateContext();
        var baseTime = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < keys.Count; i++)
        {
            var message = PaymentsApiTestData.Outbox(keys[i]);
            message.PartitionId = partitionId;
            message.CreatedAt = baseTime.AddSeconds(i);
            context.OutboxMessages.Add(message);
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Fake <see cref="ICoreBankApiClient"/> that always validates and submits successfully.</summary>
    private sealed class RecordingCoreBankApiClient : ICoreBankApiClient
    {
        private readonly ConcurrentQueue<string> _submittedTransactionIds = new();

        public IReadOnlyList<string> SubmittedTransactionIds => _submittedTransactionIds.ToArray();

        public Task<CoreBankResult<AccountValidation>> ValidateAccountAsync(
            string accountNumber, CancellationToken cancellationToken) =>
            Task.FromResult(CoreBankResult<AccountValidation>.Success(
                new AccountValidation(accountNumber, true, null, null)));

        public Task<CoreBankResult<AccountDetails>> GetAccountDetailsAsync(
            string accountNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the forwarding processor.");

        public Task<CoreBankResult<TransactionSubmission>> ProcessTransactionAsync(
            TransactionSubmissionRequest request, CancellationToken cancellationToken, bool executeInline = false)
        {
            _submittedTransactionIds.Enqueue(request.TransactionId);
            return Task.FromResult(CoreBankResult<TransactionSubmission>.Success(
                new TransactionSubmission(request.TransactionId, "Completed", DateTimeOffset.UtcNow)));
        }

        public Task<CoreBankResult<TransactionStatus>> GetTransactionStatusAsync(
            string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the forwarding processor.");
    }

    /// <summary>Fake <see cref="ICoreBankApiClient"/> whose destination account is always invalid.</summary>
    private sealed class InvalidAccountCoreBankApiClient : ICoreBankApiClient
    {
        public bool SubmitAttempted { get; private set; }

        public Task<CoreBankResult<AccountValidation>> ValidateAccountAsync(
            string accountNumber, CancellationToken cancellationToken) =>
            Task.FromResult(CoreBankResult<AccountValidation>.Success(
                new AccountValidation(accountNumber, false, null, null)));

        public Task<CoreBankResult<AccountDetails>> GetAccountDetailsAsync(
            string accountNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the forwarding processor.");

        public Task<CoreBankResult<TransactionSubmission>> ProcessTransactionAsync(
            TransactionSubmissionRequest request, CancellationToken cancellationToken, bool executeInline = false)
        {
            SubmitAttempted = true;
            return Task.FromResult(CoreBankResult<TransactionSubmission>.Success(
                new TransactionSubmission(request.TransactionId, "Completed", DateTimeOffset.UtcNow)));
        }

        public Task<CoreBankResult<TransactionStatus>> GetTransactionStatusAsync(
            string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the forwarding processor.");
    }

    private sealed class CancellationBlockingCoreBankApiClient(bool cancelDuringSubmission)
        : ICoreBankApiClient
    {
        public TaskCompletionSource CallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CoreBankResult<AccountValidation>> ValidateAccountAsync(
            string accountNumber, CancellationToken cancellationToken)
        {
            if (!cancelDuringSubmission)
            {
                CallStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return CoreBankResult<AccountValidation>.Success(
                new AccountValidation(accountNumber, true, null, null));
        }

        public Task<CoreBankResult<AccountDetails>> GetAccountDetailsAsync(
            string accountNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the forwarding processor.");

        public async Task<CoreBankResult<TransactionSubmission>> ProcessTransactionAsync(
            TransactionSubmissionRequest request, CancellationToken cancellationToken, bool executeInline = false)
        {
            CallStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation delay returned unexpectedly.");
        }

        public Task<CoreBankResult<TransactionStatus>> GetTransactionStatusAsync(
            string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the forwarding processor.");
    }

    private sealed class SingleTickLockService(TaskCompletionSource completion) : IDistributedLockService
    {
        private int _executed;
        public string? LockName { get; private set; }
        public int? LockExpirySeconds { get; private set; }

        public async Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            LockName = lockName;
            LockExpirySeconds = lockExpirySeconds;
            if (Interlocked.Exchange(ref _executed, 1) != 0)
            {
                return false;
            }

            await workload(cancellationToken);
            completion.TrySetResult();
            return true;
        }
    }

    private sealed class ConcurrentRecordingCoreBankApiClient : ICoreBankApiClient
    {
        private readonly ConcurrentQueue<string> _submittedTransactionIds = new();
        private readonly TaskCompletionSource _partition0Started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _partition1Started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> SubmittedTransactionIds => _submittedTransactionIds.ToArray();

        public Task<CoreBankResult<AccountValidation>> ValidateAccountAsync(
            string accountNumber, CancellationToken cancellationToken) =>
            Task.FromResult(CoreBankResult<AccountValidation>.Success(
                new AccountValidation(accountNumber, true, null, null)));

        public Task<CoreBankResult<AccountDetails>> GetAccountDetailsAsync(
            string accountNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the forwarding processor.");

        public async Task<CoreBankResult<TransactionSubmission>> ProcessTransactionAsync(
            TransactionSubmissionRequest request, CancellationToken cancellationToken, bool executeInline = false)
        {
            _submittedTransactionIds.Enqueue(request.TransactionId);

            if (request.TransactionId == "p0-a")
            {
                _partition0Started.TrySetResult();
                await _partition1Started.Task.WaitAsync(cancellationToken);
            }
            else if (request.TransactionId == "p1-a")
            {
                _partition1Started.TrySetResult();
                await _partition0Started.Task.WaitAsync(cancellationToken);
            }

            return CoreBankResult<TransactionSubmission>.Success(
                new TransactionSubmission(request.TransactionId, "Completed", DateTimeOffset.UtcNow));
        }

        public Task<CoreBankResult<TransactionStatus>> GetTransactionStatusAsync(
            string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the forwarding processor.");
    }

    private sealed class PollingIntervalLockService(
        TaskCompletionSource<TimeSpan> secondTick) : IDistributedLockService
    {
        private long _firstTickTimestamp;
        private int _tickCount;

        public async Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            var timestamp = Stopwatch.GetTimestamp();
            if (Interlocked.Increment(ref _tickCount) == 1)
            {
                _firstTickTimestamp = timestamp;
            }
            else
            {
                secondTick.TrySetResult(Stopwatch.GetElapsedTime(_firstTickTimestamp, timestamp));
            }

            await workload(cancellationToken);
            return true;
        }
    }

    /// <summary>
    /// Always acquires every partition's lock (letting all four run
    /// concurrently on each tick, mirroring the real lock service under no
    /// contention) and signals once partitions 0 and 1 have each completed a
    /// workload -- the two partitions this test's messages live in.
    /// </summary>
    private sealed class TwoPartitionLockService(
        TaskCompletionSource partition0Done,
        TaskCompletionSource partition1Done,
        TaskCompletionSource allPartitionsSeen) : IDistributedLockService
    {
        private readonly ConcurrentBag<string> _lockNames = new();
        private int _partitionCount;
        public IReadOnlyCollection<string> LockNames => _lockNames;

        public async Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            _lockNames.Add(lockName);
            if (Interlocked.Increment(ref _partitionCount) == 4)
            {
                allPartitionsSeen.TrySetResult();
            }

            await workload(cancellationToken).ConfigureAwait(false);

            if (lockName.EndsWith("-partition-0", StringComparison.Ordinal))
            {
                partition0Done.TrySetResult();
            }
            else if (lockName.EndsWith("-partition-1", StringComparison.Ordinal))
            {
                partition1Done.TrySetResult();
            }

            return true;
        }
    }
}
