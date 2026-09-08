using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CoreBankDemo.CoreBankAPI.Tests;

/// <summary>
/// Tier 1 (Moq against <see cref="IInboxMessageRepository"/> and
/// <see cref="IInboxMessageStore{TMessage}"/>, no real database) -- covers
/// every CoreBank-side row of spec instant-rail-timeout-cancel's matrix for
/// <see cref="TransactionCancellationHandler.CancelAsync"/>: tombstone,
/// pending-cancel, already-executed, in-flight 409, and the store races.
/// </summary>
public class TransactionCancellationHandlerTests
{
    private const string FromAccount = "NL91ABNA0417164300";
    private const string ToAccount = "NL20INGB0001234567";
    private const string TransactionId = "txn-cancel-1";

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly Mock<IInboxMessageRepository> _repository = new(MockBehavior.Strict);
    private readonly Mock<IInboxMessageStore<InboxMessage>> _inboxStore = new(MockBehavior.Strict);

    private TransactionCancellationHandler CreateHandler(int partitionCount = 4) =>
        new(_repository.Object,
            _inboxStore.Object,
            Options.Create(new InboxProcessingOptions { PartitionCount = partitionCount, LockExpirySeconds = 30 }),
            _timeProvider,
            NullLogger<TransactionCancellationHandler>.Instance);

    private static TransactionRequest Request() => new(FromAccount, ToAccount, 50m, "EUR", TransactionId);

    private InboxMessage ExistingMessage(string status, string? responsePayload = null) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = TransactionId,
        TransactionId = TransactionId,
        FromAccount = FromAccount,
        ToAccount = ToAccount,
        Amount = 50m,
        Currency = "EUR",
        PartitionId = 0,
        Status = status,
        ReceivedAt = _timeProvider.GetUtcNow().AddMinutes(-1).UtcDateTime,
        ProcessedAt = status is MessageConstants.Status.Completed or MessageConstants.Status.Cancelled
            ? _timeProvider.GetUtcNow().AddSeconds(-30).UtcDateTime
            : null,
        ResponsePayload = responsePayload
    };

    [Fact]
    public async Task CancelAsync_rejects_a_null_request()
    {
        var act = () => CreateHandler().CancelAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---- Never received: tombstone ----

    [Fact]
    public async Task CancelAsync_stores_a_cancelled_tombstone_with_the_commands_own_columns_when_nothing_was_received()
    {
        InboxMessage? stored = null;
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((m, _) => stored = m)
            .ReturnsAsync(true);
        var handler = CreateHandler(partitionCount: 4);

        var result = await handler.CancelAsync(Request(), TestContext.Current.CancellationToken, MessageConstants.Priority.Instant);

        result.Outcome.Should().Be(TransactionCancellationOutcome.Cancelled);
        result.Errors.Should().BeNull();
        result.Response.Should().Be(new TransactionResponse(TransactionId, MessageConstants.Status.Cancelled, _timeProvider.GetUtcNow()));

        stored.Should().NotBeNull();
        stored!.IdempotencyKey.Should().Be(TransactionId);
        stored.TransactionId.Should().Be(TransactionId);
        stored.FromAccount.Should().Be(FromAccount);
        stored.ToAccount.Should().Be(ToAccount);
        stored.Amount.Should().Be(50m);
        stored.Currency.Should().Be("EUR");
        stored.PartitionId.Should().Be(PartitionHelper.GetPartitionId(TransactionId, 4));
        stored.Priority.Should().Be(MessageConstants.Priority.Instant);
        stored.Status.Should().Be(MessageConstants.Status.Cancelled, "the tombstone is terminal from birth");
        stored.ReceivedAt.Should().Be(_timeProvider.GetUtcNow().UtcDateTime);
        stored.ProcessedAt.Should().Be(_timeProvider.GetUtcNow().UtcDateTime);
        stored.HoldUntil.Should().BeNull();
        stored.LastError.Should().Be(TransactionCancellationHandler.CancellationReason);
        JsonSerializer.Deserialize<TransactionResponse>(stored.ResponsePayload!).Should().Be(result.Response);

        _inboxStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancelAsync_resolves_against_the_winner_when_the_original_arrives_during_the_tombstone_store()
    {
        // AD-4 symmetry: the original beat the tombstone to the unique key.
        // The winner's row is Pending, so it is claimed and cancelled.
        var winner = ExistingMessage(MessageConstants.Status.Pending);
        var lookups = 0;
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++lookups == 1 ? null : winner);
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _inboxStore.Setup(s => s.TryClaimByIdAsync(winner.Id, It.IsAny<CancellationToken>())).ReturnsAsync(winner);
        _inboxStore.Setup(s => s.MarkAsCancelledAsync(winner, TransactionCancellationHandler.CancellationReason, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var handler = CreateHandler();

        var result = await handler.CancelAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionCancellationOutcome.Cancelled);
        lookups.Should().Be(2);
    }

    [Fact]
    public async Task CancelAsync_reports_store_failed_when_the_tombstone_loses_its_race_and_nothing_is_found()
    {
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var handler = CreateHandler();

        var result = await handler.CancelAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionCancellationOutcome.StoreFailed);
        result.Response.Should().BeNull();
        result.Errors.Should().Equal("Failed to store or retrieve transaction");
    }

    // ---- Stored, not executed: claim and cancel ----

    [Fact]
    public async Task CancelAsync_claims_and_cancels_a_pending_row_and_caches_the_cancelled_payload_on_it()
    {
        var existing = ExistingMessage(MessageConstants.Status.Pending);
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _inboxStore.Setup(s => s.TryClaimByIdAsync(existing.Id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _inboxStore.Setup(s => s.MarkAsCancelledAsync(existing, TransactionCancellationHandler.CancellationReason, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var handler = CreateHandler();

        var result = await handler.CancelAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionCancellationOutcome.Cancelled);
        result.Response.Should().Be(new TransactionResponse(TransactionId, MessageConstants.Status.Cancelled, _timeProvider.GetUtcNow()));
        JsonSerializer.Deserialize<TransactionResponse>(existing.ResponsePayload!).Should().Be(result.Response);
        _repository.Verify(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _inboxStore.Verify(s => s.TryClaimByIdAsync(existing.Id, It.IsAny<CancellationToken>()), Times.Once);
        _inboxStore.Verify(s => s.MarkAsCancelledAsync(existing, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(MessageConstants.Status.Processing)]
    [InlineData(MessageConstants.Status.Failed)]
    public async Task CancelAsync_reports_in_flight_after_losing_the_claim_race_to_a_live_claim_or_terminal_failure(string statusAfterRace)
    {
        // Matrix: "Claim lost in CoreBank -> 409 with current status".
        var existing = ExistingMessage(MessageConstants.Status.Pending);
        var afterRace = ExistingMessage(statusAfterRace);
        var lookups = 0;
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++lookups == 1 ? existing : afterRace);
        _inboxStore.Setup(s => s.TryClaimByIdAsync(existing.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        var handler = CreateHandler();

        var result = await handler.CancelAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionCancellationOutcome.InFlight);
        result.Response.Should().Be(new TransactionResponse(TransactionId, statusAfterRace, new DateTimeOffset(afterRace.ReceivedAt, TimeSpan.Zero)));
        _inboxStore.Verify(s => s.MarkAsCancelledAsync(It.IsAny<InboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelAsync_reports_the_committed_outcome_after_losing_the_claim_race_to_an_execution()
    {
        var existing = ExistingMessage(MessageConstants.Status.Pending);
        var committed = new TransactionResponse(TransactionId, MessageConstants.Status.Completed, _timeProvider.GetUtcNow());
        var afterRace = ExistingMessage(MessageConstants.Status.Completed, JsonSerializer.Serialize(committed));
        var lookups = 0;
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++lookups == 1 ? existing : afterRace);
        _inboxStore.Setup(s => s.TryClaimByIdAsync(existing.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        var handler = CreateHandler();

        var result = await handler.CancelAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionCancellationOutcome.AlreadyCommitted);
        result.Response.Should().Be(committed);
    }

    [Fact]
    public async Task CancelAsync_never_claims_twice_when_the_row_is_still_pending_after_a_lost_claim()
    {
        // Defensive: a lost claim followed by a still-Pending re-read must
        // not loop -- it is reported in flight, never claimed again.
        var existing = ExistingMessage(MessageConstants.Status.Pending);
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _inboxStore.Setup(s => s.TryClaimByIdAsync(existing.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        var handler = CreateHandler();

        var result = await handler.CancelAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionCancellationOutcome.InFlight);
        _inboxStore.Verify(s => s.TryClaimByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelAsync_reports_store_failed_when_the_row_vanishes_after_a_lost_claim()
    {
        var existing = ExistingMessage(MessageConstants.Status.Pending);
        var lookups = 0;
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++lookups == 1 ? existing : null);
        _inboxStore.Setup(s => s.TryClaimByIdAsync(existing.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        var handler = CreateHandler();

        var result = await handler.CancelAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionCancellationOutcome.StoreFailed);
    }

    [Fact]
    public async Task CancelAsync_reports_in_flight_when_the_cancel_transition_is_withheld_for_a_competing_claim()
    {
        var existing = ExistingMessage(MessageConstants.Status.Pending);
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _inboxStore.Setup(s => s.TryClaimByIdAsync(existing.Id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _inboxStore.Setup(s => s.MarkAsCancelledAsync(existing, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, string, CancellationToken>((m, _, _) => m.Status = MessageConstants.Status.Processing)
            .ReturnsAsync(MessageTransitionOutcome.Conflicted);
        var handler = CreateHandler();

        var result = await handler.CancelAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionCancellationOutcome.InFlight);
        result.Response!.Status.Should().Be(MessageConstants.Status.Processing);
    }

    [Fact]
    public async Task CancelAsync_reports_the_committed_outcome_when_the_cancel_transition_finds_the_row_already_terminal()
    {
        var existing = ExistingMessage(MessageConstants.Status.Pending);
        var committed = new TransactionResponse(TransactionId, MessageConstants.Status.Failed, _timeProvider.GetUtcNow());
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _inboxStore.Setup(s => s.TryClaimByIdAsync(existing.Id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _inboxStore.Setup(s => s.MarkAsCancelledAsync(existing, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, string, CancellationToken>((m, _, _) =>
            {
                // Mirrors MarkAsCancelledAsync's reload on a concurrency
                // conflict: the tracked row now reflects the other writer.
                m.Status = MessageConstants.Status.Completed;
                m.ResponsePayload = JsonSerializer.Serialize(committed);
            })
            .ReturnsAsync(MessageTransitionOutcome.AlreadyTerminal);
        var handler = CreateHandler();

        var result = await handler.CancelAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionCancellationOutcome.AlreadyCommitted);
        result.Response.Should().Be(committed);
    }

    // ---- Already executed / already cancelled / not cancellable ----

    [Theory]
    [InlineData(MessageConstants.Status.Completed)]
    [InlineData(MessageConstants.Status.Failed)]
    public async Task CancelAsync_reports_the_cached_committed_outcome_when_CoreBank_already_executed(string committedStatus)
    {
        var committed = new TransactionResponse(TransactionId, committedStatus, _timeProvider.GetUtcNow().AddSeconds(-30));
        var existing = ExistingMessage(MessageConstants.Status.Completed, JsonSerializer.Serialize(committed));
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        var handler = CreateHandler();

        var result = await handler.CancelAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionCancellationOutcome.AlreadyCommitted);
        result.Response.Should().Be(committed);
        _inboxStore.VerifyNoOtherCalls();
        _repository.Verify(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelAsync_reports_in_flight_for_a_completed_row_whose_payload_is_unreadable()
    {
        var existing = ExistingMessage(MessageConstants.Status.Completed, "{not-valid-json");
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        var handler = CreateHandler();

        var result = await handler.CancelAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionCancellationOutcome.InFlight);
        result.Response!.Status.Should().Be(MessageConstants.Status.Completed);
        _inboxStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancelAsync_replays_an_earlier_cancellation_verbatim()
    {
        var cached = new TransactionResponse(TransactionId, MessageConstants.Status.Cancelled, _timeProvider.GetUtcNow().AddSeconds(-30));
        var existing = ExistingMessage(MessageConstants.Status.Cancelled, JsonSerializer.Serialize(cached));
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        var handler = CreateHandler();

        var result = await handler.CancelAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionCancellationOutcome.Cancelled);
        result.Response.Should().Be(cached);
        _inboxStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancelAsync_replays_a_cancelled_row_without_a_payload_from_its_processed_at()
    {
        var existing = ExistingMessage(MessageConstants.Status.Cancelled, responsePayload: null);
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        var handler = CreateHandler();

        var result = await handler.CancelAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionCancellationOutcome.Cancelled);
        result.Response.Should().Be(new TransactionResponse(
            TransactionId, MessageConstants.Status.Cancelled, new DateTimeOffset(existing.ProcessedAt!.Value, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(MessageConstants.Status.Processing)]
    [InlineData(MessageConstants.Status.Failed)]
    public async Task CancelAsync_reports_in_flight_and_never_touches_a_processing_or_failed_row(string status)
    {
        // Boundary: never cancel a row that is Processing or Failed.
        var existing = ExistingMessage(status);
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        var handler = CreateHandler();

        var result = await handler.CancelAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionCancellationOutcome.InFlight);
        result.Response.Should().Be(new TransactionResponse(TransactionId, status, new DateTimeOffset(existing.ReceivedAt, TimeSpan.Zero)));
        _inboxStore.VerifyNoOtherCalls();
    }
}
