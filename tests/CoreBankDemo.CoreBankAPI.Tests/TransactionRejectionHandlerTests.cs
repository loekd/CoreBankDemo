using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
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

/// <summary>
/// Tier 1 (Moq against <see cref="IInboxMessageRepository"/>, no real
/// database) for <see cref="TransactionRejectionHandler.RejectAsync"/>
/// (ADR-023): every outcome, the enqueue-before-store order that puts the
/// rejection and its <c>transaction.failed</c> event in one save, and the
/// detach discipline for an event whose rejection never committed. The
/// enqueuer is mocked to add its row to a connection-less
/// <see cref="CoreBankDbContext"/> (<see cref="CoreBankApiUnitTestSupport.DetachedDbContext"/>),
/// like the sibling <see cref="TransactionCancellationHandlerTests"/>; the
/// atomicity of the save itself and the column limits are proved on
/// PostgreSQL in tier 2.
/// </summary>
public sealed class TransactionRejectionHandlerTests : IDisposable
{
    private const string FromAccount = "NL91ABNA0417164300";
    private const string ToAccount = "NL20INGB0001234567";
    private const string TransactionId = "txn-rejected-1";
    private static readonly string[] Errors = ["Amount must be between 0.01 and 1,000,000"];

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly Mock<IInboxMessageRepository> _repository = new(MockBehavior.Strict);
    private readonly Mock<IOutboxEventEnqueuer> _enqueuer = new(MockBehavior.Strict);
    private readonly CoreBankDbContext _dbContext = CoreBankApiUnitTestSupport.DetachedDbContext();
    private readonly BusinessMetrics _businessMetrics = new();

    /// <summary>What happened, in order: <c>enqueue</c> and <c>store</c>.</summary>
    private readonly List<string> _calls = [];
    private readonly List<(MessagingOutboxMessage Row, string? Reason)> _enqueued = [];

    public TransactionRejectionHandlerTests()
    {
        _enqueuer
            .Setup(e => e.EnqueueTransactionFailedAsync(It.IsAny<InboxMessage>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<InboxMessage, string?, CancellationToken>((message, reason, _) =>
            {
                var row = new MessagingOutboxMessage
                {
                    Id = Guid.NewGuid(),
                    PartitionId = 0,
                    IdempotencyKey = message.TransactionId,
                    TransactionId = message.TransactionId,
                    Status = MessageConstants.Status.Pending,
                    EventType = Constants.TransactionFailed,
                    EventSource = "https://corebank-api/transactions",
                    AccountNumber = message.FromAccount,
                    ToAccount = message.ToAccount,
                    Amount = message.Amount,
                    Currency = message.Currency,
                    TransactionStatus = MessageConstants.Status.Failed,
                    ErrorReason = reason,
                    CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    EventOccurredAt = message.ProcessedAt ?? DateTime.MinValue
                };
                _dbContext.MessagingOutboxMessages.Add(row);
                _enqueued.Add((row, reason));
                _calls.Add("enqueue");
                return Task.CompletedTask;
            });
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _businessMetrics.Dispose();
    }

    private TransactionRejectionHandler CreateHandler(int partitionCount = 4) =>
        new(_repository.Object,
            _enqueuer.Object,
            _dbContext,
            Options.Create(new InboxProcessingOptions { PartitionCount = partitionCount, LockExpirySeconds = 30 }),
            _timeProvider,
            _businessMetrics,
            NullLogger<TransactionRejectionHandler>.Instance);

    private static IEnumerable<string> Describe(MetricsTestListener listener) =>
        listener.Measurements.Select(m => $"{m.InstrumentName} {m.Tags["messaging.store.name"]} {m.Tags["outcome"]}");

    private static TransactionRequest Request() => new(FromAccount, ToAccount, 0m, "EUR", TransactionId);

    private void SetUpNoExistingRow() =>
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);

    private Func<InboxMessage?> SetUpStore(bool stored)
    {
        InboxMessage? captured = null;
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((message, _) =>
            {
                captured = message;
                _calls.Add("store");
            })
            .ReturnsAsync(stored);
        return () => captured;
    }

    [Fact]
    public async Task RejectAsync_stores_a_terminal_row_with_a_failed_response_after_enqueueing_its_event()
    {
        SetUpNoExistingRow();
        var stored = SetUpStore(stored: true);
        using var listener = new MetricsTestListener(_businessMetrics);

        var outcome = await CreateHandler().RejectAsync(Request(), Errors, TestContext.Current.CancellationToken);

        outcome.Should().Be(TransactionRejectionOutcome.Recorded);
        // The rejected row leaves the inbox as completed (what the execution
        // handler records for a business rejection) and its transaction.failed
        // event enters the messaging outbox, both exactly once.
        Describe(listener).Should().BeEquivalentTo(
            $"{BusinessMetrics.MessagingItemsProcessedInstrumentName} corebank-inbox completed",
            $"{BusinessMetrics.MessagingStoreOperationsInstrumentName} corebank-outbox added");
        _calls.Should().Equal(["enqueue", "store"], "StoreIfNewAsync's single save must carry the event row");
        var now = _timeProvider.GetUtcNow();
        var row = stored()!;
        row.IdempotencyKey.Should().Be(TransactionId);
        row.TransactionId.Should().Be(TransactionId);
        row.FromAccount.Should().Be(FromAccount);
        row.ToAccount.Should().Be(ToAccount);
        row.Amount.Should().Be(0m);
        row.Currency.Should().Be("EUR");
        row.PartitionId.Should().Be(PartitionHelper.GetPartitionId(TransactionId, 4));
        row.Status.Should().Be(MessageConstants.Status.Completed);
        row.ReceivedAt.Should().Be(now.UtcDateTime);
        row.ProcessedAt.Should().Be(now.UtcDateTime);
        row.LastError.Should().Be(Errors[0]);
        var cached = JsonSerializer.Deserialize<TransactionResponse>(row.ResponsePayload!)!;
        cached.TransactionId.Should().Be(TransactionId);
        cached.Status.Should().Be(MessageConstants.Status.Failed);
        cached.ProcessedAt.Should().Be(now);

        var (evt, reason) = _enqueued.Should().ContainSingle().Subject;
        reason.Should().Be(Errors[0]);
        _dbContext.Entry(evt).State.Should().Be(EntityState.Added, "a committed rejection keeps its event");
    }

    [Fact]
    public async Task RejectAsync_joins_every_error_into_the_reason_and_uses_the_configured_partition_count()
    {
        SetUpNoExistingRow();
        var stored = SetUpStore(stored: true);

        await CreateHandler(partitionCount: 16)
            .RejectAsync(Request(), ["Amount out of range", "Currency must be 3 uppercase letters"], TestContext.Current.CancellationToken);

        stored()!.LastError.Should().Be("Amount out of range; Currency must be 3 uppercase letters");
        stored()!.PartitionId.Should().Be(PartitionHelper.GetPartitionId(TransactionId, 16));
        _enqueued.Single().Reason.Should().Be("Amount out of range; Currency must be 3 uppercase letters");
    }

    [Fact]
    public async Task RejectAsync_clamps_values_that_do_not_fit_the_table()
    {
        SetUpNoExistingRow();
        var stored = SetUpStore(stored: true);
        var request = new TransactionRequest(new string('A', 80), null!, -5m, null!, TransactionId);

        var outcome = await CreateHandler().RejectAsync(request, Errors, TestContext.Current.CancellationToken);

        outcome.Should().Be(TransactionRejectionOutcome.Recorded);
        stored()!.FromAccount.Should().Be(new string('A', 50));
        stored()!.ToAccount.Should().BeEmpty();
        stored()!.Amount.Should().Be(0m);
        stored()!.Currency.Should().Be("EUR");
    }

    [Fact]
    public async Task RejectAsync_never_overwrites_an_id_corebank_already_knows()
    {
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InboxMessage
            {
                Id = Guid.NewGuid(),
                IdempotencyKey = TransactionId,
                TransactionId = TransactionId,
                FromAccount = FromAccount,
                ToAccount = ToAccount,
                Amount = 50m,
                Currency = "EUR",
                Status = MessageConstants.Status.Pending,
                ReceivedAt = _timeProvider.GetUtcNow().UtcDateTime
            });
        using var listener = new MetricsTestListener(_businessMetrics);

        var outcome = await CreateHandler().RejectAsync(Request(), Errors, TestContext.Current.CancellationToken);

        outcome.Should().Be(TransactionRejectionOutcome.AlreadyKnown);
        listener.Measurements.Should().BeEmpty();
        _enqueued.Should().BeEmpty();
        _repository.Verify(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RejectAsync_reports_already_known_and_drops_its_event_when_it_loses_the_unique_key_race()
    {
        SetUpNoExistingRow();
        SetUpStore(stored: false);
        using var listener = new MetricsTestListener(_businessMetrics);

        var outcome = await CreateHandler().RejectAsync(Request(), Errors, TestContext.Current.CancellationToken);

        outcome.Should().Be(TransactionRejectionOutcome.AlreadyKnown);
        listener.Measurements.Should().BeEmpty();
        _dbContext.Entry(_enqueued.Single().Row).State.Should().Be(EntityState.Detached);
    }

    [Fact]
    public async Task RejectAsync_reports_store_failed_and_drops_its_event_when_the_save_throws()
    {
        SetUpNoExistingRow();
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        using var listener = new MetricsTestListener(_businessMetrics);

        var outcome = await CreateHandler().RejectAsync(Request(), Errors, TestContext.Current.CancellationToken);

        outcome.Should().Be(TransactionRejectionOutcome.StoreFailed);
        listener.Measurements.Should().BeEmpty();
        _dbContext.Entry(_enqueued.Single().Row).State.Should().Be(EntityState.Detached);
    }

    [Fact]
    public async Task RejectAsync_only_drops_the_event_of_the_rejection_that_failed()
    {
        SetUpNoExistingRow();
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var unrelated = new MessagingOutboxMessage
        {
            Id = Guid.NewGuid(),
            PartitionId = 0,
            IdempotencyKey = "txn-other",
            TransactionId = "txn-other",
            Status = MessageConstants.Status.Pending,
            EventType = Constants.TransactionFailed,
            EventSource = "https://corebank-api/transactions",
            AccountNumber = FromAccount,
            ToAccount = ToAccount,
            Amount = 1m,
            Currency = "EUR",
            TransactionStatus = MessageConstants.Status.Failed,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            EventOccurredAt = _timeProvider.GetUtcNow().UtcDateTime
        };
        _dbContext.MessagingOutboxMessages.Add(unrelated);

        await CreateHandler().RejectAsync(Request(), Errors, TestContext.Current.CancellationToken);

        _dbContext.Entry(unrelated).State.Should().Be(EntityState.Added);
    }

    [Fact]
    public async Task RejectAsync_reports_store_failed_when_the_lookup_throws()
    {
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var outcome = await CreateHandler().RejectAsync(Request(), Errors, TestContext.Current.CancellationToken);

        outcome.Should().Be(TransactionRejectionOutcome.StoreFailed);
        _enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task RejectAsync_propagates_caller_cancellation_unchanged_and_drops_its_event()
    {
        using var cts = new CancellationTokenSource();
        SetUpNoExistingRow();
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns<InboxMessage, CancellationToken>((_, _) =>
            {
                cts.Cancel();
                return Task.FromException<bool>(new OperationCanceledException(cts.Token));
            });
        using var listener = new MetricsTestListener(_businessMetrics);

        var act = () => CreateHandler().RejectAsync(Request(), Errors, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        listener.Measurements.Should().BeEmpty();
        _dbContext.Entry(_enqueued.Single().Row).State.Should().Be(
            EntityState.Detached, "the event of a rejection that never committed must not ride along on a later save");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectAsync_cannot_record_a_request_without_a_usable_transaction_id(string? transactionId)
    {
        using var listener = new MetricsTestListener(_businessMetrics);

        var outcome = await CreateHandler().RejectAsync(
            new TransactionRequest(FromAccount, ToAccount, 10m, "EUR", transactionId!), Errors, TestContext.Current.CancellationToken);

        outcome.Should().Be(TransactionRejectionOutcome.NotRecordable);
        listener.Measurements.Should().BeEmpty();
        _repository.VerifyNoOtherCalls();
        _enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task RejectAsync_cannot_record_a_too_long_transaction_id_or_a_missing_body()
    {
        var handler = CreateHandler();

        (await handler.RejectAsync(
            new TransactionRequest(FromAccount, ToAccount, 10m, "EUR", new string('x', 101)), Errors, TestContext.Current.CancellationToken))
            .Should().Be(TransactionRejectionOutcome.NotRecordable);
        (await handler.RejectAsync(null, Errors, TestContext.Current.CancellationToken))
            .Should().Be(TransactionRejectionOutcome.NotRecordable);
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RejectAsync_requires_the_errors()
    {
        var act = () => CreateHandler().RejectAsync(Request(), null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
