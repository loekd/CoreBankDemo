using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.CoreBankApi;

public class InboxProcessorTests(PostgresContainerFixture fixture) : CoreBankApiPostgresTestBase(fixture)
{
    private const string FromAccount = "NL91ABNA0417164300";
    private const string ToAccount = "NL20INGB0001234567";
    private const string TransactionId = "txn-123";

    [Fact]
    public async Task StartAsync_claims_dispatches_and_persists_all_success_side_effects_atomically()
    {
        await SeedAccountsAndMessageAsync();

        using var services = BuildHandlerServices();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = CreateProcessor(services.GetRequiredService<IServiceScopeFactory>(), completion);

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        await using var verifyContext = CreateContext();
        var persistedMessage = await verifyContext.InboxMessages
            .AsNoTracking()
            .SingleAsync(m => m.TransactionId == TransactionId, TestContext.Current.CancellationToken);
        var fromAccount = await verifyContext.Accounts
            .AsNoTracking()
            .SingleAsync(a => a.AccountNumber == FromAccount, TestContext.Current.CancellationToken);
        var toAccount = await verifyContext.Accounts
            .AsNoTracking()
            .SingleAsync(a => a.AccountNumber == ToAccount, TestContext.Current.CancellationToken);
        var outboxRows = await verifyContext.MessagingOutboxMessages
            .AsNoTracking()
            .OrderBy(m => m.EventType)
            .ThenBy(m => m.AccountNumber)
            .ToListAsync(TestContext.Current.CancellationToken);

        fromAccount.Balance.Should().Be(50m);
        toAccount.Balance.Should().Be(75m);
        persistedMessage.Status.Should().Be(MessageConstants.Status.Completed);
        persistedMessage.ProcessedAt.Should().Be(TimeProvider.GetUtcNow().UtcDateTime);
        JsonSerializer.Deserialize<TransactionResponse>(persistedMessage.ResponsePayload!)
            .Should()
            .Be(new TransactionResponse(TransactionId, MessageConstants.Status.Completed, TimeProvider.GetUtcNow()));
        outboxRows.Should().HaveCount(3);
        outboxRows.Count(m => m.EventType == Constants.TransactionCompleted).Should().Be(1);
        outboxRows.Count(m => m.EventType == Constants.BalanceUpdated).Should().Be(2);
        outboxRows.Should().AllSatisfy(row =>
            row.EventOccurredAt.Should().Be(persistedMessage.ProcessedAt));
    }

    [Fact]
    public async Task StartAsync_when_handler_throws_rolls_back_handler_side_effects_and_marks_message_for_retry()
    {
        await SeedAccountsAndMessageAsync();

        using var services = BuildHandlerServices(scopedServices =>
        {
            scopedServices.AddScoped<IOutboxEventEnqueuer, ThrowingAfterFirstAddOutboxEventEnqueuer>();
        });
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = CreateProcessor(services.GetRequiredService<IServiceScopeFactory>(), completion);

        await processor.StartAsync(TestContext.Current.CancellationToken);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);

        await using var verifyContext = CreateContext();
        var persistedMessage = await verifyContext.InboxMessages
            .AsNoTracking()
            .SingleAsync(m => m.TransactionId == TransactionId, TestContext.Current.CancellationToken);
        var fromAccount = await verifyContext.Accounts
            .AsNoTracking()
            .SingleAsync(a => a.AccountNumber == FromAccount, TestContext.Current.CancellationToken);
        var toAccount = await verifyContext.Accounts
            .AsNoTracking()
            .SingleAsync(a => a.AccountNumber == ToAccount, TestContext.Current.CancellationToken);
        var outboxCount = await verifyContext.MessagingOutboxMessages.CountAsync(TestContext.Current.CancellationToken);

        persistedMessage.Status.Should().Be(MessageConstants.Status.Pending);
        persistedMessage.RetryCount.Should().Be(1);
        persistedMessage.LastError.Should().Be("boom during enqueue");
        persistedMessage.ProcessedAt.Should().BeNull();
        persistedMessage.ResponsePayload.Should().BeNull();
        fromAccount.Balance.Should().Be(100m);
        toAccount.Balance.Should().Be(25m);
        outboxCount.Should().Be(0);
    }

    private async Task SeedAccountsAndMessageAsync()
    {
        await using var context = CreateContext();
        context.Accounts.AddRange(
            new Account
            {
                AccountNumber = FromAccount,
                AccountHolderName = "From Holder",
                Balance = 100m,
                Currency = "EUR",
                IsActive = true,
                CreatedAt = TimeProvider.GetUtcNow().UtcDateTime
            },
            new Account
            {
                AccountNumber = ToAccount,
                AccountHolderName = "To Holder",
                Balance = 25m,
                Currency = "EUR",
                IsActive = true,
                CreatedAt = TimeProvider.GetUtcNow().UtcDateTime
            });
        context.InboxMessages.Add(NewMessage());
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private InboxProcessor CreateProcessor(
        IServiceScopeFactory scopeFactory,
        TaskCompletionSource completion) =>
        new(
            new SingleTickLockService(completion),
            scopeFactory,
            new ActivitySource(nameof(InboxProcessorTests)),
            TimeProvider,
            NullLogger<InboxProcessor>.Instance,
            TestBusinessMetrics.Instance,
            Options.Create(new InboxProcessingOptions
            {
                PartitionCount = 1,
                LockExpirySeconds = 30,
                PollingIntervalMs = 60000
            }));

    private ServiceProvider BuildHandlerServices(Action<IServiceCollection>? overrideScopedServices = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider);
        services.AddSingleton(TestBusinessMetrics.Instance);
        services.AddSingleton<IOptions<MessagingOutboxProcessingOptions>>(Options.Create(new MessagingOutboxProcessingOptions
        {
            PartitionCount = 4,
            LockExpirySeconds = 30,
            PollingIntervalMs = 5000
        }));
        services.AddScoped<CoreBankDbContext>(_ => CreateContext());
        services.AddScoped<InboxMessageRepository>();
        services.AddScoped<IInboxMessageRepository>(sp => sp.GetRequiredService<InboxMessageRepository>());
        services.AddScoped<IInboxMessageStore<InboxMessage>>(sp => sp.GetRequiredService<InboxMessageRepository>());
        services.AddScoped<ITransactionExecutor, InProcessTransactionExecutor>();
        services.AddScoped<IOutboxEventEnqueuer, OutboxEventEnqueuer>();
        overrideScopedServices?.Invoke(services);
        services.AddScoped<IInboxMessageHandler<InboxMessage>, TransactionExecutionHandler>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private InboxMessage NewMessage() => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = TransactionId,
        TransactionId = TransactionId,
        FromAccount = FromAccount,
        ToAccount = ToAccount,
        Amount = 50m,
        Currency = "EUR",
        PartitionId = 0,
        Status = MessageConstants.Status.Pending,
        ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime,
        TraceParent = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01",
        TraceState = "congo=t61rcWkgMzE"
    };

    private sealed class SingleTickLockService(TaskCompletionSource completion) : IDistributedLockService
    {
        public async Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            await workload(cancellationToken);
            completion.TrySetResult();
            return true;
        }
    }

    /// <summary>
    /// Applies the balance moves through the same tracked <see cref="CoreBankDbContext"/>
    /// the handler commits, without taking the production row lock: this class
    /// proves the processor's atomicity contract, while
    /// <see cref="AccountRowLockTests"/> proves the real
    /// <c>SELECT ... FOR UPDATE</c> path.
    /// </summary>
    private sealed class InProcessTransactionExecutor(CoreBankDbContext dbContext, TimeProvider timeProvider) : ITransactionExecutor
    {
        public async Task<TransactionExecutionResult> ExecuteAsync(
            string fromAccountNumber,
            string toAccountNumber,
            decimal amount,
            string transactionId,
            CancellationToken cancellationToken)
        {
            var fromAccount = await dbContext.Accounts.SingleAsync(
                account => account.AccountNumber == fromAccountNumber,
                cancellationToken);
            var toAccount = await dbContext.Accounts.SingleAsync(
                account => account.AccountNumber == toAccountNumber,
                cancellationToken);

            var processedAt = timeProvider.GetUtcNow();
            fromAccount.Balance -= amount;
            fromAccount.UpdatedAt = processedAt.UtcDateTime;
            toAccount.Balance += amount;
            toAccount.UpdatedAt = processedAt.UtcDateTime;

            return new TransactionExecutionResult(
                true,
                new TransactionResponse(transactionId, MessageConstants.Status.Completed, processedAt),
                null,
                fromAccount.Balance,
                toAccount.Balance);
        }
    }

    private sealed class ThrowingAfterFirstAddOutboxEventEnqueuer(CoreBankDbContext dbContext) : IOutboxEventEnqueuer
    {
        public Task EnqueueTransactionCompletedAsync(InboxMessage message, CancellationToken ct)
        {
            dbContext.MessagingOutboxMessages.Add(new MessagingOutboxMessage
            {
                Id = Guid.NewGuid(),
                PartitionId = 0,
                IdempotencyKey = message.TransactionId,
                TransactionId = message.TransactionId,
                Status = MessageConstants.Status.Pending,
                EventType = Constants.TransactionCompleted,
                EventSource = "https://corebank-api/transactions",
                AccountNumber = message.FromAccount,
                ToAccount = message.ToAccount,
                Amount = message.Amount,
                Currency = message.Currency,
                TransactionStatus = MessageConstants.Status.Completed,
                CreatedAt = DateTime.UtcNow,
                EventOccurredAt = message.ProcessedAt!.Value
            });

            throw new InvalidOperationException("boom during enqueue");
        }

        public Task EnqueueTransactionFailedAsync(InboxMessage message, string? errorReason, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task EnqueueBalanceUpdatedAsync(InboxMessage message, string accountNumber, decimal delta, decimal newBalance, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
