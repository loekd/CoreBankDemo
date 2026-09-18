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
/// Tier 2 (real PostgreSQL) for ADR-023's rule that a <c>400</c> always has a
/// durable record behind it: a request CoreBank refuses at the door is stored
/// as a terminal inbox row carrying a <c>Failed</c> response, together with
/// its <c>transaction.failed</c> event, in <c>StoreIfNewAsync</c>'s single
/// <c>SaveChanges</c>. Proven on the real unique index and column limits: an
/// id CoreBank already knows is never overwritten, values that do not fit the
/// table are clamped, and a failed save leaves no event row behind. The clock
/// ticks on every read, so the cached payload's <c>ProcessedAt</c> is proven
/// equal to the row's against a moving clock.
/// </summary>
public class TransactionRejectionHandlerTests(PostgresContainerFixture fixture) : CoreBankApiPostgresTestBase(fixture)
{
    private const string FromAccount = "NL91ABNA0417164300";
    private const string ToAccount = "NL20INGB0001234567";
    private const string TransactionId = "txn-rejected-1";

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

    private readonly TickingTimeProvider _clock = new(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));

    private static readonly string[] Errors = ["Amount must be between 0.01 and 1,000,000"];

    private TransactionRejectionHandler CreateHandler(CoreBankDbContext context, IInboxMessageRepository repository) =>
        new(repository,
            new OutboxEventEnqueuer(
                context,
                Options.Create(new MessagingOutboxProcessingOptions { PartitionCount = 4, LockExpirySeconds = 30, PollingIntervalMs = 5000 }),
                _clock),
            context,
            Options.Create(new InboxProcessingOptions { PartitionCount = 4, LockExpirySeconds = 30 }),
            _clock,
            NullLogger<TransactionRejectionHandler>.Instance);

    [Fact]
    public async Task A_rejection_and_its_failed_event_commit_in_one_save()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, _clock, TestBusinessMetrics.Instance);
        var handler = CreateHandler(context, repository);
        var request = new TransactionRequest(FromAccount, ToAccount, 0m, "EUR", TransactionId);

        var outcome = await handler.RejectAsync(request, Errors, ct);

        outcome.Should().Be(TransactionRejectionOutcome.Recorded);
        await using var verification = CreateContext();
        var inbox = await verification.InboxMessages.SingleAsync(ct);
        inbox.TransactionId.Should().Be(TransactionId);
        inbox.Status.Should().Be(MessageConstants.Status.Completed);
        inbox.ProcessedAt.Should().NotBeNull();
        inbox.LastError.Should().Be(Errors[0]);
        var cached = JsonSerializer.Deserialize<TransactionResponse>(inbox.ResponsePayload!)!;
        cached.Status.Should().Be(MessageConstants.Status.Failed);
        cached.ProcessedAt.UtcDateTime.Should().Be(inbox.ProcessedAt!.Value);
        var evt = await verification.MessagingOutboxMessages.SingleAsync(ct);
        evt.EventType.Should().Be(Constants.TransactionFailed);
        evt.TransactionId.Should().Be(TransactionId);
        evt.TransactionStatus.Should().Be(MessageConstants.Status.Failed);
        evt.ErrorReason.Should().Be(Errors[0]);
        evt.Status.Should().Be(MessageConstants.Status.Pending);
    }

    [Fact]
    public async Task An_id_corebank_already_knows_is_never_overwritten_and_gets_no_event()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var seed = CreateContext())
        {
            seed.InboxMessages.Add(new InboxMessage
            {
                Id = Guid.NewGuid(), IdempotencyKey = TransactionId, TransactionId = TransactionId,
                FromAccount = FromAccount, ToAccount = ToAccount, Amount = 50m, Currency = "EUR",
                PartitionId = 0, Status = MessageConstants.Status.Pending, ReceivedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync(ct);
        }

        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, _clock, TestBusinessMetrics.Instance);
        var outcome = await CreateHandler(context, repository)
            .RejectAsync(new TransactionRequest(FromAccount, ToAccount, 0m, "EUR", TransactionId), Errors, ct);

        outcome.Should().Be(TransactionRejectionOutcome.AlreadyKnown);
        await using var verification = CreateContext();
        (await verification.InboxMessages.SingleAsync(ct)).Status.Should().Be(MessageConstants.Status.Pending);
        (await verification.MessagingOutboxMessages.CountAsync(ct)).Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_request_without_a_usable_transaction_id_is_not_recordable(string? transactionId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, _clock, TestBusinessMetrics.Instance);

        var outcome = await CreateHandler(context, repository)
            .RejectAsync(new TransactionRequest(FromAccount, ToAccount, 10m, "EUR", transactionId!), Errors, ct);

        outcome.Should().Be(TransactionRejectionOutcome.NotRecordable);
        (await context.InboxMessages.CountAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task A_too_long_transaction_id_and_a_null_request_are_not_recordable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, _clock, TestBusinessMetrics.Instance);
        var handler = CreateHandler(context, repository);

        (await handler.RejectAsync(new TransactionRequest(FromAccount, ToAccount, 10m, "EUR", new string('x', 101)), Errors, ct))
            .Should().Be(TransactionRejectionOutcome.NotRecordable);
        (await handler.RejectAsync(null, Errors, ct)).Should().Be(TransactionRejectionOutcome.NotRecordable);
    }

    [Fact]
    public async Task Values_that_do_not_fit_the_table_are_clamped_and_the_reason_keeps_the_truth()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, _clock, TestBusinessMetrics.Instance);
        var request = new TransactionRequest(new string('A', 80), null!, 99_999_999_999_999_999m, "EURO", TransactionId);

        var outcome = await CreateHandler(context, repository).RejectAsync(request, ["FromAccount too long", "ToAccount required"], ct);

        outcome.Should().Be(TransactionRejectionOutcome.Recorded);
        await using var verification = CreateContext();
        var inbox = await verification.InboxMessages.SingleAsync(ct);
        inbox.FromAccount.Should().HaveLength(50);
        inbox.ToAccount.Should().BeEmpty();
        inbox.Amount.Should().Be(0m);
        inbox.Currency.Should().Be("EUR");
        inbox.LastError.Should().Be("FromAccount too long; ToAccount required");
    }

    [Fact]
    public async Task A_failed_save_reports_store_failed_and_leaves_nothing_behind()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new Mock<IInboxMessageRepository>(MockBehavior.Strict);
        repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>())).ReturnsAsync((InboxMessage?)null);
        repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var outcome = await CreateHandler(context, repository.Object)
            .RejectAsync(new TransactionRequest(FromAccount, ToAccount, 0m, "EUR", TransactionId), Errors, ct);

        outcome.Should().Be(TransactionRejectionOutcome.StoreFailed);
        context.ChangeTracker.Entries<MessagingOutboxMessage>().Should().BeEmpty("the event row must not ride along on a later save");
    }
}
