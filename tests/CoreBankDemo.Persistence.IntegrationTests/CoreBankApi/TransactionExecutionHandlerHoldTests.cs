using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.CoreBankApi;

/// <summary>
/// The planted flaw behind <c>Demo:LockRelease = afterPublish</c>: the executor's session
/// holds (per account under row locking, per partition under partition locking) are kept
/// by the handler past its own commit until the transfer's outcome events have left the
/// messaging outbox. Proved against real PostgreSQL by watching <c>pg_locks</c> from a
/// second connection; <c>Demo:LockStrategy</c> only decides which key is held.
/// </summary>
public class TransactionExecutionHandlerHoldTests(PostgresContainerFixture fixture) : CoreBankApiPostgresTestBase(fixture)
{
    private const string FromAccount = "NL91ABNA0417164300";
    private const string ToAccount = "NL20INGB0001234567";
    private const string TransactionId = "txn-hold";
    private const int PartitionCount = 4;
    private const int AccountKeySpace = 2;
    private const int PartitionKeySpace = 1;

    private static readonly TimeSpan ObservationWindow = TimeSpan.FromMilliseconds(500);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterPublish_release_holds_the_keys_past_commit_until_the_outbox_rows_are_completed(bool partitionLocking)
    {
        var strategy = partitionLocking ? LockStrategy.Partition : LockStrategy.Row;
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        await using var observer = CreateContext();
        var (handler, message) = await ArrangeAsync(context, Options.Create(new DemoOptions { LockStrategy = strategy, LockRelease = LockRelease.AfterPublish }), ct);

        var handling = handler.HandleAsync(message, ct);

        var key = await WaitForHoldAsync(observer, strategy, ct);
        (await observer.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, ct)).Status
            .Should().Be(MessageConstants.Status.Completed, "the transfer is committed while the key is still held");
        (await Task.WhenAny(handling, Task.Delay(ObservationWindow, ct))).Should().NotBe(handling,
            "the handler keeps the hold until the outcome events are published");

        await observer.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"MessagingOutboxMessages\" SET \"Status\" = {MessageConstants.Status.Completed} WHERE \"TransactionId\" = {TransactionId}", ct);

        await handling.WaitAsync(PostgresContainerFixture.LockWaitTimeout, ct);
        (await HoldsAsync(observer, key, ct)).Should().Be(0, "the hold is released once the events are out");
    }

    [Fact]
    public async Task AfterPublish_release_gives_back_the_holds_and_completes_normally_when_the_wait_is_cancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var context = CreateContext();
        await using var observer = CreateContext();
        var (handler, message) = await ArrangeAsync(context, Options.Create(new DemoOptions { LockRelease = LockRelease.AfterPublish }), ct);

        var handling = handler.HandleAsync(message, cancellation.Token);
        var key = await WaitForHoldAsync(observer, LockStrategy.Row, ct);

        await cancellation.CancelAsync();

        // The transfer is already committed, so a cancelled wait is not a failed transfer.
        await handling.WaitAsync(PostgresContainerFixture.LockWaitTimeout, ct);
        (await HoldsAsync(observer, key, ct)).Should().Be(0);
        (await observer.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, ct)).Status
            .Should().Be(MessageConstants.Status.Completed);
    }

    [Fact]
    public async Task AfterPublish_release_gives_up_the_wait_after_the_configured_cap()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        await using var observer = CreateContext();
        var options = Options.Create(new DemoOptions { LockRelease = LockRelease.AfterPublish, PublishWaitMilliseconds = 300 });
        var (handler, message) = await ArrangeAsync(context, options, ct);

        var handling = handler.HandleAsync(message, ct);
        var key = await WaitForHoldAsync(observer, LockStrategy.Row, ct);

        await handling.WaitAsync(PostgresContainerFixture.LockWaitTimeout, ct);
        (await HoldsAsync(observer, key, ct)).Should().Be(0, "the cap bounds how long a key can be held");
        (await observer.MessagingOutboxMessages.AsNoTracking().CountAsync(m => m.TransactionId == TransactionId && m.Status == MessageConstants.Status.Pending, ct))
            .Should().Be(1, "giving up never touches the outbox rows");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commit_release_takes_no_session_hold_and_does_not_wait_for_the_outbox(bool partitionLocking)
    {
        var strategy = partitionLocking ? LockStrategy.Partition : LockStrategy.Row;
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        await using var observer = CreateContext();
        var (handler, message) = await ArrangeAsync(context, Options.Create(new DemoOptions { LockStrategy = strategy }), ct);

        await handler.HandleAsync(message, ct).WaitAsync(ObservationWindow, ct);

        (await observer.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND granted AND classid IN ({AccountKeySpace}, {PartitionKeySpace})").SingleAsync(ct))
            .Should().Be(0);
        (await observer.MessagingOutboxMessages.AsNoTracking().CountAsync(m => m.TransactionId == TransactionId && m.Status == MessageConstants.Status.Pending, ct))
            .Should().Be(1);
    }

    /// <summary>
    /// A handler whose executor mimics the real one's holds (through the real
    /// <see cref="AccountRepository"/> on the handler's own context, which is what makes
    /// them session holds on the pinned connection) and whose enqueuer adds one real
    /// Pending outbox row, so the hold has something to wait for.
    /// </summary>
    private async Task<(TransactionExecutionHandler Handler, InboxMessage Message)> ArrangeAsync(
        CoreBankDbContext context, IOptions<DemoOptions> demoOptions, CancellationToken ct)
    {
        var message = new InboxMessage
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = TransactionId,
            TransactionId = TransactionId,
            FromAccount = FromAccount,
            ToAccount = ToAccount,
            Amount = 50m,
            Currency = "EUR",
            PartitionId = PartitionHelper.GetPartitionId(FromAccount, PartitionCount),
            Status = MessageConstants.Status.Pending,
            ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime
        };
        context.InboxMessages.Add(message);
        await context.SaveChangesAsync(ct);

        var accountRepository = new AccountRepository(context);
        var response = new TransactionResponse(TransactionId, MessageConstants.Status.Completed, TimeProvider.GetUtcNow());
        var result = new TransactionExecutionResult(true, response, null, 50m, 75m);
        var executor = new Mock<ITransactionExecutor>(MockBehavior.Strict);
        executor.Setup(x => x.ExecuteAsync(FromAccount, ToAccount, 50m, TransactionId, It.IsAny<CancellationToken>()))
            .Returns(async (string from, string to, decimal _, string _, CancellationToken token) =>
            {
                if (demoOptions.Value.LockRelease == LockRelease.AfterPublish)
                {
                    if (demoOptions.Value.LockStrategy == LockStrategy.Partition)
                    {
                        foreach (var partitionId in new[] { from, to }.Select(a => PartitionHelper.GetPartitionId(a, PartitionCount)).Distinct().Order())
                        {
                            await accountRepository.HoldPartitionAsync(partitionId, token);
                        }
                    }
                    else
                    {
                        foreach (var account in new[] { from, to }.Order(StringComparer.Ordinal))
                        {
                            await accountRepository.HoldAccountAsync(account, token);
                        }
                    }
                }

                return result;
            });

        var enqueuer = new Mock<IOutboxEventEnqueuer>(MockBehavior.Strict);
        enqueuer.Setup(x => x.EnqueueTransactionCompletedAsync(message, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                context.MessagingOutboxMessages.Add(new MessagingOutboxMessage
                {
                    Id = Guid.NewGuid(),
                    PartitionId = PartitionHelper.GetPartitionId(TransactionId, PartitionCount),
                    IdempotencyKey = TransactionId,
                    TransactionId = TransactionId,
                    Status = MessageConstants.Status.Pending,
                    EventType = "transaction.completed",
                    EventSource = "https://corebank-api/transactions",
                    AccountNumber = FromAccount,
                    ToAccount = ToAccount,
                    Amount = 50m,
                    Currency = "EUR",
                    TransactionStatus = MessageConstants.Status.Completed,
                    CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
                    EventOccurredAt = TimeProvider.GetUtcNow().UtcDateTime
                });
                return Task.CompletedTask;
            });
        enqueuer.Setup(x => x.EnqueueBalanceUpdatedAsync(message, It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var businessMetrics = new BusinessMetrics();
        var repository = new InboxMessageRepository(context, TimeProvider, businessMetrics);
        var handler = new TransactionExecutionHandler(
            executor.Object, enqueuer.Object, repository, context, TimeProvider, businessMetrics, accountRepository, demoOptions);
        return (handler, message);
    }

    /// <summary>The (classid, objid) pg_locks key the strategy's first hold takes for this transfer.</summary>
    private static async Task<(int ClassId, long ObjId)> KeyAsync(CoreBankDbContext observer, LockStrategy strategy, CancellationToken ct) =>
        strategy == LockStrategy.Partition
            ? (PartitionKeySpace, PartitionHelper.GetPartitionId(FromAccount, PartitionCount))
            : (AccountKeySpace, await observer.Database.SqlQuery<int>($"SELECT hashtext({FromAccount}) AS \"Value\"").SingleAsync(ct));

    private static async Task<(int ClassId, long ObjId)> WaitForHoldAsync(CoreBankDbContext observer, LockStrategy strategy, CancellationToken ct)
    {
        var key = await KeyAsync(observer, strategy, ct);
        var deadline = DateTime.UtcNow + PostgresContainerFixture.LockWaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await HoldsAsync(observer, key, ct) > 0)
            {
                return key;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
        }

        throw new TimeoutException($"no session hold on key {key} appeared in pg_locks");
    }

    /// <summary>
    /// Granted session advisory locks on the key (two-key form: classid = key space, objid =
    /// key). pg_locks exposes objid as an unsigned 32-bit value, so a negative hashtext key is
    /// compared through its unsigned image.
    /// </summary>
    private static async Task<int> HoldsAsync(CoreBankDbContext observer, (int ClassId, long ObjId) key, CancellationToken ct)
    {
        var objid = key.ObjId < 0 ? key.ObjId + (1L << 32) : key.ObjId;
        return await observer.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND granted AND classid = {key.ClassId} AND objid = {objid}")
            .SingleAsync(ct);
    }
}
