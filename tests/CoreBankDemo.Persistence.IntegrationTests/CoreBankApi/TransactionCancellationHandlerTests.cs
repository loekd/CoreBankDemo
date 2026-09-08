using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.CoreBankApi;

/// <summary>
/// Tier 2 (real PostgreSQL) for spec instant-rail-cancelled-event's atomicity
/// rule: a <c>transaction.cancelled</c> outbox row exists if and only if the
/// cancel it describes committed. Both cancel paths end in exactly one
/// <c>SaveChanges</c> (<c>StoreIfNewAsync</c>, <c>MarkAsCancelledAsync</c>);
/// these tests prove on the real unique index and concurrency token that a
/// lost race leaves zero event rows and a usable context, and a won one
/// leaves exactly one. The clock ticks on every read
/// (<see cref="TickingTimeProvider"/>), so "the event's EventOccurredAt equals
/// the row's ProcessedAt equals the cached payload's ProcessedAt" is proven
/// against a moving clock rather than assumed from a frozen one.
/// </summary>
public class TransactionCancellationHandlerTests(PostgresContainerFixture fixture) : CoreBankApiPostgresTestBase(fixture)
{
    private const string FromAccount = "NL91ABNA0417164300";
    private const string ToAccount = "NL20INGB0001234567";
    private const string TransactionId = "txn-cancel-event-1";

    /// <summary>Advances by one millisecond on every read, like a real clock would between two calls.</summary>
    private sealed class TickingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            var value = _now;
            _now = _now.AddMilliseconds(1);
            return value;
        }
    }

    private readonly TickingTimeProvider _clock = new(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));

    private static TransactionRequest Request() => new(FromAccount, ToAccount, 50m, "EUR", TransactionId);

    [Fact]
    public async Task A_tombstone_and_its_cancelled_event_commit_in_one_save()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, _clock, TestBusinessMetrics.Instance);
        var handler = CreateHandler(context, repository, repository);

        var result = await handler.CancelAsync(Request(), ct, MessageConstants.Priority.Instant);

        result.Outcome.Should().Be(TransactionCancellationOutcome.Cancelled);
        await using var verification = CreateContext();
        var inbox = await verification.InboxMessages.SingleAsync(ct);
        inbox.Status.Should().Be(MessageConstants.Status.Cancelled);
        inbox.ProcessedAt.Should().Be(result.Response!.ProcessedAt.UtcDateTime, "the tombstone carries the cached payload's own instant");
        var evt = await verification.MessagingOutboxMessages.SingleAsync(ct);
        evt.EventType.Should().Be(Constants.TransactionCancelled);
        evt.Status.Should().Be(MessageConstants.Status.Pending);
        evt.TransactionId.Should().Be(TransactionId);
        evt.AccountNumber.Should().Be(FromAccount);
        evt.TransactionStatus.Should().Be(MessageConstants.Status.Cancelled);
        evt.ErrorReason.Should().Be(TransactionCancellationHandler.CancellationReason);
        evt.EventOccurredAt.Should().Be(inbox.ProcessedAt);
        evt.EventOccurredAt.Should().Be(result.Response!.ProcessedAt.UtcDateTime);
        evt.PartitionId.Should().Be(PartitionHelper.GetPartitionId(TransactionId, 4));
    }

    [Fact]
    public async Task A_tombstone_that_loses_the_unique_key_race_leaves_zero_event_rows_and_a_usable_context()
    {
        var ct = TestContext.Current.CancellationToken;
        var committed = new TransactionResponse(TransactionId, MessageConstants.Status.Completed, TimeProvider.GetUtcNow());
        await SeedAsync(MessageConstants.Status.Completed, JsonSerializer.Serialize(committed), ct);

        await using var context = CreateContext();
        var real = new InboxMessageRepository(context, _clock, TestBusinessMetrics.Instance);
        // The original arrives between the handler's lookup and its insert:
        // the first lookup sees nothing, the insert then hits the real unique
        // index on IdempotencyKey.
        var racing = RacingRepository(real);
        var handler = CreateHandler(context, racing.Object, real);

        var result = await handler.CancelAsync(Request(), ct);

        result.Outcome.Should().Be(TransactionCancellationOutcome.AlreadyCommitted);
        result.Response.Should().Be(committed);
        context.ChangeTracker.Entries<MessagingOutboxMessage>().Should().BeEmpty("the event row was detached with the tombstone");
        // Usable: a further save on the same context must not resurrect the event.
        await context.SaveChangesAsync(ct);
        await using var verification = CreateContext();
        (await verification.MessagingOutboxMessages.CountAsync(ct)).Should().Be(0);
        (await verification.InboxMessages.CountAsync(ct)).Should().Be(1);
    }

    [Fact]
    public async Task A_tombstone_that_loses_to_a_pending_original_cancels_it_with_exactly_one_event_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(MessageConstants.Status.Pending, null, ct);

        await using var context = CreateContext();
        var real = new InboxMessageRepository(context, _clock, TestBusinessMetrics.Instance);
        var racing = RacingRepository(real);
        var handler = CreateHandler(context, racing.Object, real);

        var result = await handler.CancelAsync(Request(), ct);

        result.Outcome.Should().Be(TransactionCancellationOutcome.Cancelled);
        await using var verification = CreateContext();
        var inbox = await verification.InboxMessages.SingleAsync(ct);
        inbox.Status.Should().Be(MessageConstants.Status.Cancelled);
        var evt = await verification.MessagingOutboxMessages.SingleAsync(ct);
        evt.EventType.Should().Be(Constants.TransactionCancelled);
        evt.EventOccurredAt.Should().Be(result.Response!.ProcessedAt.UtcDateTime);
    }

    [Fact]
    public async Task A_pending_row_cancel_commits_the_cancel_and_its_event_in_one_save()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(MessageConstants.Status.Pending, null, ct);

        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, _clock, TestBusinessMetrics.Instance);
        var handler = CreateHandler(context, repository, repository);

        var result = await handler.CancelAsync(Request(), ct);

        result.Outcome.Should().Be(TransactionCancellationOutcome.Cancelled);
        await using var verification = CreateContext();
        var inbox = await verification.InboxMessages.SingleAsync(ct);
        inbox.Status.Should().Be(MessageConstants.Status.Cancelled);
        inbox.LastError.Should().Be(TransactionCancellationHandler.CancellationReason);
        JsonSerializer.Deserialize<TransactionResponse>(inbox.ResponsePayload!).Should().Be(result.Response);
        var evt = await verification.MessagingOutboxMessages.SingleAsync(ct);
        evt.EventType.Should().Be(Constants.TransactionCancelled);
        evt.TransactionStatus.Should().Be(MessageConstants.Status.Cancelled);
        evt.ErrorReason.Should().Be(TransactionCancellationHandler.CancellationReason);
        evt.EventOccurredAt.Should().Be(result.Response!.ProcessedAt.UtcDateTime, "the event carries the cached cancellation time");
        evt.EventOccurredAt.Should().Be(inbox.ProcessedAt,
            "MarkAsCancelledAsync keeps the pre-stamped instant even though the clock ticked between enqueue and save");
        evt.CreatedAt.Should().BeAfter(evt.EventOccurredAt, "the clock genuinely moved between the stamp and the enqueue");
    }

    [Fact]
    public async Task A_pending_cancel_that_loses_to_a_concurrent_execution_leaves_zero_event_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(MessageConstants.Status.Pending, null, ct);
        var committed = new TransactionResponse(TransactionId, MessageConstants.Status.Completed, TimeProvider.GetUtcNow());

        await using var context = CreateContext();
        var real = new InboxMessageRepository(context, _clock, TestBusinessMetrics.Instance);
        // Between the handler's claim and its cancel, another writer commits
        // the row: the cancel's save then hits the Status concurrency token,
        // MarkAsCancelledAsync reloads and reports AlreadyTerminal, and the
        // event row -- part of that rolled-back save -- must be gone.
        var store = new Mock<IInboxMessageStore<InboxMessage>>(MockBehavior.Strict);
        store.Setup(s => s.TryClaimByIdAsync(seeded.Id, It.IsAny<CancellationToken>()))
            .Returns<Guid, CancellationToken>((id, token) => real.TryClaimByIdAsync(id, token));
        store.Setup(s => s.MarkAsCancelledAsync(It.IsAny<InboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<InboxMessage, string, CancellationToken>(async (message, reason, token) =>
            {
                await using var other = CreateContext();
                var theirs = await other.InboxMessages.SingleAsync(m => m.Id == message.Id, token);
                theirs.Status = MessageConstants.Status.Completed;
                theirs.ProcessedAt = TimeProvider.GetUtcNow().UtcDateTime;
                theirs.ResponsePayload = JsonSerializer.Serialize(committed);
                await other.SaveChangesAsync(token);
                return await real.MarkAsCancelledAsync(message, reason, token);
            });
        var handler = CreateHandler(context, real, store.Object);

        var result = await handler.CancelAsync(Request(), ct);

        result.Outcome.Should().Be(TransactionCancellationOutcome.AlreadyCommitted);
        result.Response.Should().Be(committed);
        context.ChangeTracker.Entries<MessagingOutboxMessage>().Should().BeEmpty();
        await using var verification = CreateContext();
        (await verification.MessagingOutboxMessages.CountAsync(ct)).Should().Be(0);
        (await verification.InboxMessages.SingleAsync(ct)).Status.Should().Be(MessageConstants.Status.Completed);
    }

    private TransactionCancellationHandler CreateHandler(
        CoreBankDbContext context,
        IInboxMessageRepository repository,
        IInboxMessageStore<InboxMessage> store) =>
        new(repository,
            store,
            new OutboxEventEnqueuer(
                context,
                Options.Create(new MessagingOutboxProcessingOptions { PartitionCount = 4, LockExpirySeconds = 30, PollingIntervalMs = 5000 }),
                _clock),
            context,
            Options.Create(new InboxProcessingOptions { PartitionCount = 4, LockExpirySeconds = 30 }),
            _clock,
            NullLogger<TransactionCancellationHandler>.Instance);

    /// <summary>First lookup misses (as if the row did not exist yet); everything else is the real repository.</summary>
    private static Mock<IInboxMessageRepository> RacingRepository(InboxMessageRepository real)
    {
        var lookups = 0;
        var racing = new Mock<IInboxMessageRepository>(MockBehavior.Strict);
        racing.Setup(r => r.FindByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((key, token) =>
                ++lookups == 1 ? Task.FromResult<InboxMessage?>(null) : real.FindByIdempotencyKeyAsync(key, token));
        racing.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns<InboxMessage, CancellationToken>((message, token) => real.StoreIfNewAsync(message, token));
        return racing;
    }

    private async Task<InboxMessage> SeedAsync(string status, string? responsePayload, CancellationToken ct)
    {
        await using var seed = CreateContext();
        var message = new InboxMessage
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = TransactionId,
            TransactionId = TransactionId,
            FromAccount = FromAccount,
            ToAccount = ToAccount,
            Amount = 50m,
            Currency = "EUR",
            PartitionId = PartitionHelper.GetPartitionId(TransactionId, 4),
            Status = status,
            ReceivedAt = TimeProvider.GetUtcNow().AddMinutes(-1).UtcDateTime,
            ProcessedAt = status == MessageConstants.Status.Completed ? TimeProvider.GetUtcNow().AddSeconds(-30).UtcDateTime : null,
            ResponsePayload = responsePayload
        };
        seed.InboxMessages.Add(message);
        await seed.SaveChangesAsync(ct);
        return message;
    }
}
