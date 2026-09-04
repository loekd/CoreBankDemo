using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.LoadTestSupport;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;
using CoreBankDemo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.LoadTestSupport;

[Collection("Processor start gate Redis")]
public sealed class LoadTestDatabaseResetterTests(
    PostgresContainerFixture fixture,
    RedisContainerFixture redis) : IAsyncLifetime
{
    private string? _coreBankConnectionString;
    private string? _paymentsConnectionString;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _coreBankConnectionString = await fixture.CreateDatabaseAsync("loadresetcore", cancellationToken);
        _paymentsConnectionString = await fixture.CreateDatabaseAsync("loadresetpayments", cancellationToken);

        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        await coreBank.Database.EnsureCreatedAsync(cancellationToken);
        await payments.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_coreBankConnectionString is not null)
        {
            await fixture.DropDatabaseAsync(_coreBankConnectionString);
        }

        if (_paymentsConnectionString is not null)
        {
            await fixture.DropDatabaseAsync(_paymentsConnectionString);
        }
    }

    [Fact]
    public async Task Reset_commits_both_databases_and_restores_load_account_balances()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        coreBank.Accounts.Add(new Account
        {
            AccountNumber = "NL01LOAD0000000001",
            AccountHolderName = "Load Account",
            Balance = 123m,
            Currency = "EUR",
            IsActive = true,
            CreatedAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 8, 30, 1, 0, 0, DateTimeKind.Utc)
        });
        coreBank.InboxMessages.Add(new CoreBankDemo.CoreBankAPI.Inbox.InboxMessage
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = "core-inbox",
            PartitionId = 0,
            ReceivedAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
            FromAccount = "from",
            ToAccount = "to",
            Amount = 1m,
            Currency = "EUR",
            TransactionId = "core-inbox"
        });
        coreBank.MessagingOutboxMessages.Add(new MessagingOutboxMessage
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = "core-outbox",
            PartitionId = 0,
            Status = MessageConstants.Status.Pending,
            CreatedAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
            EventOccurredAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
            TransactionId = "core-outbox",
            EventType = "test.event",
            EventSource = "test",
            AccountNumber = "from",
            ToAccount = "to",
            Amount = 1m,
            Currency = "EUR",
            TransactionStatus = MessageConstants.Status.Completed
        });
        payments.OutboxMessages.Add(PaymentsApiTestData.Outbox("payments-outbox"));
        payments.InboxMessages.Add(PaymentsApiTestData.Inbox(
            "payments-inbox", "test.event", "account"));
        await coreBank.SaveChangesAsync(cancellationToken);
        await payments.SaveChangesAsync(cancellationToken);

        var resetter = new LoadTestDatabaseResetter(coreBank, payments);

        var result = await resetter.ResetAsync(cancellationToken);

        result.AccountsReset.Should().Be(1);
        result.TotalBalance.Should().Be(LoadTestConstants.InitialBalance);
        coreBank.ChangeTracker.Clear();
        var account = await coreBank.Accounts.AsNoTracking().SingleAsync(cancellationToken);
        account.Balance.Should().Be(LoadTestConstants.InitialBalance);
        account.UpdatedAt.Should().BeNull();
        (await coreBank.InboxMessages.CountAsync(cancellationToken)).Should().Be(0);
        (await coreBank.MessagingOutboxMessages.CountAsync(cancellationToken)).Should().Be(0);
        (await payments.OutboxMessages.CountAsync(cancellationToken)).Should().Be(0);
        (await payments.InboxMessages.CountAsync(cancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Reset_releases_four_real_gates_once_after_both_databases_commit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        await using var payments = CreatePaymentsContext();
        coreBank.Accounts.Add(new Account
        {
            AccountNumber = "NL02LOAD0000000002",
            AccountHolderName = "Second Load Account",
            Balance = 1m,
            Currency = "EUR",
            IsActive = true,
            CreatedAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc)
        });
        payments.OutboxMessages.Add(PaymentsApiTestData.Outbox("assembled-reset"));
        await coreBank.SaveChangesAsync(cancellationToken);
        await payments.SaveChangesAsync(cancellationToken);

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var redisDatabase = multiplexer.GetDatabase();
        await redisDatabase.KeyDeleteAsync([
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
        var coordinator = new DatabaseResetCoordinator(
            new LoadTestDatabaseResetter(coreBank, payments),
            publisher,
            new DatabaseResetState());

        var first = await coordinator.ResetAndReleaseAsync(cancellationToken);
        await Task.WhenAll(waits);
        var generation = await redisDatabase.StringGetAsync(RedisProcessorStartGate.GenerationKey);

        payments.OutboxMessages.Add(new CoreBankDemo.PaymentsAPI.Outbox.OutboxMessage
        {
            IdempotencyKey = "between-runs",
            TransactionId = "between-runs",
            FromAccount = "NL01LOAD0000000001",
            ToAccount = "NL02LOAD0000000002",
            Amount = 1m,
            Currency = "EUR",
            PartitionId = 0,
            Status = CoreBankDemo.Messaging.MessageConstants.Status.Pending,
            CreatedAt = TimeProvider.System.GetUtcNow().UtcDateTime,
        });
        await payments.SaveChangesAsync(cancellationToken);

        var second = await coordinator.ResetAndReleaseAsync(cancellationToken);

        second.Should().Be(first);
        (await redisDatabase.StringGetAsync(RedisProcessorStartGate.GenerationKey)).Should().Be(generation);
        (await redisDatabase.SetLengthAsync(
            $"corebankdemo:processor-start:acknowledgements:{generation}")).Should().Be(4);
        coreBank.ChangeTracker.Clear();
        payments.ChangeTracker.Clear();
        (await coreBank.Accounts.SingleAsync(
            account => account.AccountNumber == "NL02LOAD0000000002", cancellationToken))
            .Balance.Should().Be(LoadTestConstants.InitialBalance);
        (await payments.OutboxMessages.CountAsync(cancellationToken)).Should().Be(0);
    }

    private CoreBankDbContext CreateCoreBankContext() =>
        new(new DbContextOptionsBuilder<CoreBankDbContext>()
            .UseNpgsql(_coreBankConnectionString)
            .Options);

    private PaymentsDbContext CreatePaymentsContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(_paymentsConnectionString)
            .Options);
}
