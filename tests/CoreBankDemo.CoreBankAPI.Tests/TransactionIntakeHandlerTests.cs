using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CoreBankDemo.CoreBankAPI.Tests;

/// <summary>
/// Tier 1 (Moq against <see cref="IInboxMessageRepository"/>, no real
/// database) — covers every row of spec-4-4's I/O &amp; Edge-Case Matrix for
/// <see cref="TransactionIntakeHandler.ProcessAsync"/>.
/// </summary>
public class TransactionIntakeHandlerTests
{
    private const string FromAccount = "NL91ABNA0417164300";
    private const string ToAccount = "NL20INGB0001234567";
    private const string TransactionId = "txn-123";

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly Mock<IInboxMessageRepository> _repository = new(MockBehavior.Strict);
    private readonly Mock<IInboxMessageStore<InboxMessage>> _inboxStore = new(MockBehavior.Strict);
    private readonly Mock<IInboxMessageHandler<InboxMessage>> _executionHandler = new(MockBehavior.Strict);
    private readonly TestLockService _lock = new();
    private readonly BusinessMetrics _businessMetrics = new();

    private TransactionIntakeHandler CreateHandler(int partitionCount = 4) =>
        new(_repository.Object,
            _inboxStore.Object,
            _executionHandler.Object,
            _lock,
            Options.Create(new InboxProcessingOptions { PartitionCount = partitionCount, LockExpirySeconds = 30 }),
            _timeProvider,
            NullLogger<TransactionIntakeHandler>.Instance,
            _businessMetrics);

    /// <summary>Sets up a successful inline claim of whatever row <see cref="_repository"/>'s <c>StoreIfNewAsync</c> stores.</summary>
    private void SetUpSuccessfulClaimOf(Func<InboxMessage?> stored) =>
        _inboxStore
            .Setup(s => s.TryClaimByIdIfOldestAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

    private static TransactionRequest ValidRequest(string transactionId = TransactionId) =>
        new(FromAccount, ToAccount, 50m, "EUR", transactionId);

    [Fact]
    public async Task ProcessAsync_stores_a_pending_row_and_returns_accepted_for_a_fresh_transaction_id()
    {
        InboxMessage? stored = null;
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((m, _) => stored = m)
            .ReturnsAsync(true);

        var handler = CreateHandler(partitionCount: 4);
        var request = ValidRequest();

        var result = await handler.ProcessAsync(request, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionIntakeOutcome.Accepted);
        result.Errors.Should().BeNull();
        result.Response.Should().Be(new TransactionResponse(TransactionId, MessageConstants.Status.Pending, _timeProvider.GetUtcNow()));

        stored.Should().NotBeNull();
        stored!.IdempotencyKey.Should().Be(TransactionId);
        stored.TransactionId.Should().Be(TransactionId);
        stored.FromAccount.Should().Be(FromAccount);
        stored.ToAccount.Should().Be(ToAccount);
        stored.Amount.Should().Be(50m);
        stored.Currency.Should().Be("EUR");
        stored.Status.Should().Be(MessageConstants.Status.Pending);
        stored.ReceivedAt.Should().Be(_timeProvider.GetUtcNow().UtcDateTime);
        stored.PartitionId.Should().Be(PartitionHelper.GetPartitionId(TransactionId, 4));

        _repository.Verify(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessAsync_uses_the_configured_partition_count_when_computing_the_partition_id()
    {
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        InboxMessage? stored = null;
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((m, _) => stored = m)
            .ReturnsAsync(true);

        var handler = CreateHandler(partitionCount: 7);

        await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken);

        stored!.PartitionId.Should().Be(PartitionHelper.GetPartitionId(TransactionId, 7));
    }

    [Fact]
    public async Task ProcessAsync_replays_the_cached_response_verbatim_for_a_completed_duplicate()
    {
        var cachedResponse = new TransactionResponse(TransactionId, MessageConstants.Status.Completed, _timeProvider.GetUtcNow().AddMinutes(-5));
        var existing = ExistingMessage(MessageConstants.Status.Completed, responsePayload: JsonSerializer.Serialize(cachedResponse));

        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionIntakeOutcome.Replayed);
        result.Response.Should().Be(cachedResponse);
        result.Errors.Should().BeNull();

        _repository.Verify(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()), Times.Once);
        _repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(MessageConstants.Status.Pending)]
    [InlineData(MessageConstants.Status.Processing)]
    public async Task ProcessAsync_returns_in_flight_with_current_status_for_a_pending_or_processing_duplicate(string status)
    {
        var existing = ExistingMessage(status);
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionIntakeOutcome.InFlight);
        result.Errors.Should().BeNull();
        result.Response.Should().Be(new TransactionResponse(TransactionId, status, new DateTimeOffset(existing.ReceivedAt, TimeSpan.Zero)));
    }

    [Fact]
    public async Task ProcessAsync_returns_transport_failed_with_the_last_error_for_a_failed_duplicate()
    {
        var existing = ExistingMessage(MessageConstants.Status.Failed, lastError: "boom");
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionIntakeOutcome.TransportFailed);
        result.Response.Should().BeNull();
        result.Errors.Should().Equal("boom");
    }

    [Fact]
    public async Task ProcessAsync_falls_back_to_a_default_error_message_when_a_failed_duplicate_has_no_last_error()
    {
        var existing = ExistingMessage(MessageConstants.Status.Failed, lastError: null);
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionIntakeOutcome.TransportFailed);
        result.Errors.Should().Equal("Transaction failed");
    }

    [Fact]
    public async Task ProcessAsync_re_queries_and_branches_as_found_when_it_loses_the_store_race()
    {
        var winner = ExistingMessage(MessageConstants.Status.Pending);
        _repository.SetupSequence(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null)
            .ReturnsAsync(winner);
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionIntakeOutcome.InFlight);
        result.Response.Should().Be(new TransactionResponse(TransactionId, MessageConstants.Status.Pending, new DateTimeOffset(winner.ReceivedAt, TimeSpan.Zero)));

        _repository.Verify(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()), Times.Exactly(2));
        _repository.Verify(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessAsync_never_crashes_when_the_store_race_loser_finds_nothing_on_re_query()
    {
        _repository.SetupSequence(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null)
            .ReturnsAsync((InboxMessage?)null);
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionIntakeOutcome.TransportFailed);
        result.Response.Should().BeNull();
        result.Errors.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ProcessAsync_falls_through_to_in_flight_when_a_completed_duplicates_response_payload_is_null_or_empty()
    {
        var existing = ExistingMessage(MessageConstants.Status.Completed, responsePayload: null);
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken);

        // Defensive edge case (spec-4-4 matrix): a Completed row with a
        // null/empty payload must not crash on deserialize. There is no
        // dedicated "corrupt data" outcome for POST, so it is reported the
        // same way any other non-terminal-looking status would be.
        result.Outcome.Should().Be(TransactionIntakeOutcome.InFlight);
        result.Response!.Status.Should().Be(MessageConstants.Status.Completed);
    }

    [Fact]
    public async Task ProcessAsync_falls_through_to_in_flight_when_a_completed_duplicates_response_payload_is_corrupt()
    {
        var existing = ExistingMessage(MessageConstants.Status.Completed, responsePayload: "{not-valid-json");
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken);

        // Same defensive fallback as a null/empty payload: malformed JSON must
        // never crash the request (AD-5 guarantees this "should not happen").
        result.Outcome.Should().Be(TransactionIntakeOutcome.InFlight);
        result.Response!.Status.Should().Be(MessageConstants.Status.Completed);
    }

    // ---- Story 6.5: business metrics ----

    [Fact]
    public async Task ProcessAsync_records_an_accepted_transaction_intake_metric_for_a_fresh_transaction_id()
    {
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        using var listener = new MetricsTestListener(_businessMetrics);

        await CreateHandler().ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken);

        listener.Measurements.Should().ContainSingle(
            m => m.InstrumentName == "corebankdemo.transaction.intake")
            .Which.Tags["outcome"].Should().Be("accepted");
    }

    [Fact]
    public async Task ProcessAsync_records_a_replayed_transaction_intake_metric_for_a_completed_duplicate()
    {
        var cachedResponse = new TransactionResponse(TransactionId, MessageConstants.Status.Completed, _timeProvider.GetUtcNow());
        var existing = ExistingMessage(MessageConstants.Status.Completed, responsePayload: JsonSerializer.Serialize(cachedResponse));
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        using var listener = new MetricsTestListener(_businessMetrics);

        await CreateHandler().ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken);

        listener.Measurements.Should().ContainSingle(
            m => m.InstrumentName == "corebankdemo.transaction.intake")
            .Which.Tags["outcome"].Should().Be("replayed");
    }

    [Fact]
    public async Task ProcessAsync_records_an_in_flight_transaction_intake_metric_for_a_pending_duplicate()
    {
        var existing = ExistingMessage(MessageConstants.Status.Pending);
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        using var listener = new MetricsTestListener(_businessMetrics);

        await CreateHandler().ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken);

        listener.Measurements.Should().ContainSingle(
            m => m.InstrumentName == "corebankdemo.transaction.intake")
            .Which.Tags["outcome"].Should().Be("in_flight");
    }

    [Fact]
    public async Task ProcessAsync_records_a_transport_failed_transaction_intake_metric_for_a_failed_duplicate()
    {
        var existing = ExistingMessage(MessageConstants.Status.Failed, lastError: "boom");
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        using var listener = new MetricsTestListener(_businessMetrics);

        await CreateHandler().ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken);

        listener.Measurements.Should().ContainSingle(
            m => m.InstrumentName == "corebankdemo.transaction.intake")
            .Which.Tags["outcome"].Should().Be("transport_failed");
    }

    // ---- Spec: add-instant-payment-rail -- X-Execute-Mode: inline ----

    [Fact]
    public async Task ProcessAsync_reproduces_deferred_behaviour_exactly_when_execute_inline_is_omitted()
    {
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(TransactionIntakeOutcome.Accepted);
        _inboxStore.VerifyNoOtherCalls();
        _executionHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessAsync_executes_inline_and_returns_the_committed_response_when_it_commits()
    {
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        InboxMessage? stored = null;
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((m, _) => stored = m)
            .ReturnsAsync(true);
        SetUpSuccessfulClaimOf(() => stored);
        var committedResponse = new TransactionResponse(TransactionId, MessageConstants.Status.Completed, _timeProvider.GetUtcNow());
        _executionHandler
            .Setup(h => h.HandleAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((m, _) =>
            {
                m.Status = MessageConstants.Status.Completed;
                m.ResponsePayload = JsonSerializer.Serialize(committedResponse);
            })
            .Returns(Task.CompletedTask);

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken, executeInline: true);

        result.Outcome.Should().Be(TransactionIntakeOutcome.InlineCompleted);
        result.Response.Should().Be(committedResponse);
        result.Errors.Should().BeNull();
        _inboxStore.Verify(s => s.TryClaimByIdIfOldestAsync(stored!.Id, stored.PartitionId, It.IsAny<CancellationToken>()), Times.Once);
        _executionHandler.Verify(h => h.HandleAsync(stored!, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_falls_back_to_pending_and_never_executes_when_the_inline_claim_is_lost()
    {
        // A concurrent background batch-claim already owns the row (spec:
        // add-instant-payment-rail, review loop 1) -- the execution handler
        // must never be invoked without owning the claim.
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _inboxStore
            .Setup(s => s.TryClaimByIdIfOldestAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken, executeInline: true);

        result.Outcome.Should().Be(TransactionIntakeOutcome.Accepted);
        result.Response!.Status.Should().Be(MessageConstants.Status.Pending);
        _executionHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessAsync_falls_back_to_pending_when_inline_execution_throws()
    {
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        InboxMessage? stored = null;
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((m, _) => stored = m)
            .ReturnsAsync(true);
        SetUpSuccessfulClaimOf(() => stored);
        _executionHandler
            .Setup(h => h.HandleAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ledger transaction rolled back"));
        _inboxStore.Setup(s => s.MarkAsFailedWithRetryAsync(
                It.IsAny<InboxMessage>(),
                "ledger transaction rolled back",
                It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, string, CancellationToken>((message, _, _) => message.Status = MessageConstants.Status.Pending)
            .ReturnsAsync(MessageTransitionOutcome.Applied);

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken, executeInline: true);

        result.Outcome.Should().Be(TransactionIntakeOutcome.Accepted);
        result.Response!.Status.Should().Be(MessageConstants.Status.Pending);
        stored!.Status.Should().Be(MessageConstants.Status.Pending);
    }

    [Fact]
    public async Task ProcessAsync_reports_transport_failed_when_inline_execution_throws_and_retries_are_exhausted()
    {
        // Patch 3 regression test: when MarkAsFailedWithRetryAsync drives the
        // claimed row to terminal Failed (retries exhausted), ProcessAsync
        // must reflect that -- not the generic Accepted/Pending response it
        // would otherwise return whenever TryExecuteInlineAsync returns null.
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        InboxMessage? stored = null;
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((m, _) => stored = m)
            .ReturnsAsync(true);
        SetUpSuccessfulClaimOf(() => stored);
        _executionHandler
            .Setup(h => h.HandleAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ledger transaction rolled back"));
        _inboxStore.Setup(s => s.MarkAsFailedWithRetryAsync(
                It.IsAny<InboxMessage>(),
                "ledger transaction rolled back",
                It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, string, CancellationToken>((message, error, _) =>
            {
                message.Status = MessageConstants.Status.Failed;
                message.LastError = error;
            })
            .ReturnsAsync(MessageTransitionOutcome.Applied);

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken, executeInline: true);

        result.Outcome.Should().Be(TransactionIntakeOutcome.TransportFailed);
        result.Response.Should().BeNull();
        result.Errors.Should().Equal("ledger transaction rolled back");
        stored!.Status.Should().Be(MessageConstants.Status.Failed);
    }

    [Fact]
    public async Task ProcessAsync_returns_the_inline_completed_result_when_lock_ownership_is_lost_after_execution_commits()
    {
        // Patch 1 regression test: ExecuteWithLockAsync returns false both
        // when the lock was never acquired (callback never ran) AND when the
        // callback ran to completion but ownership was lost mid-flight. The
        // second case must still return the real, committed result -- not
        // silently downgrade a completed inline execution to Pending.
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        InboxMessage? stored = null;
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((m, _) => stored = m)
            .ReturnsAsync(true);
        SetUpSuccessfulClaimOf(() => stored);
        var committedResponse = new TransactionResponse(TransactionId, MessageConstants.Status.Completed, _timeProvider.GetUtcNow());
        _executionHandler
            .Setup(h => h.HandleAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((m, _) =>
            {
                m.Status = MessageConstants.Status.Completed;
                m.ResponsePayload = JsonSerializer.Serialize(committedResponse);
            })
            .Returns(Task.CompletedTask);
        _lock.LoseOwnershipAfterWorkload = true;

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken, executeInline: true);

        result.Outcome.Should().Be(TransactionIntakeOutcome.InlineCompleted,
            "execution genuinely committed even though lock ownership was reported lost afterward");
        result.Response.Should().Be(committedResponse);
    }

    [Fact]
    public async Task ProcessAsync_falls_back_to_pending_gracefully_when_the_lock_backend_throws()
    {
        // Patch 2 regression test: an exception from ExecuteWithLockAsync
        // itself (e.g. a Redis connection failure) must degrade gracefully
        // to the standard Accepted/Pending response, matching every other
        // transport hiccup in this method, rather than propagating.
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _lock.ThrowException = new InvalidOperationException("redis connection failure");

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken, executeInline: true);

        result.Outcome.Should().Be(TransactionIntakeOutcome.Accepted);
        result.Response!.Status.Should().Be(MessageConstants.Status.Pending);
        _inboxStore.VerifyNoOtherCalls();
        _executionHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessAsync_falls_back_to_pending_when_inline_execution_completes_without_a_deserializable_response()
    {
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        InboxMessage? stored = null;
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((m, _) => stored = m)
            .ReturnsAsync(true);
        SetUpSuccessfulClaimOf(() => stored);
        // Defensive edge case: handler returns normally but never actually
        // committed (should not happen given AD-5's atomic commit).
        _executionHandler
            .Setup(h => h.HandleAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken, executeInline: true);

        result.Outcome.Should().Be(TransactionIntakeOutcome.Accepted);
        result.Response!.Status.Should().Be(MessageConstants.Status.Pending);
    }

    [Fact]
    public async Task ProcessAsync_propagates_caller_cancellation_from_inline_execution_unchanged()
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new OperationCanceledException(cancellation.Token);
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        InboxMessage? stored = null;
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((m, _) => stored = m)
            .ReturnsAsync(true);
        SetUpSuccessfulClaimOf(() => stored);
        _executionHandler
            .Setup(h => h.HandleAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expected);
        await cancellation.CancelAsync();

        var handler = CreateHandler();

        var act = () => handler.ProcessAsync(ValidRequest(), cancellation.Token, executeInline: true);

        var assertion = await act.Should().ThrowAsync<OperationCanceledException>();
        assertion.Which.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task ProcessAsync_never_executes_inline_for_a_duplicate()
    {
        var existing = ExistingMessage(MessageConstants.Status.Pending);
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var handler = CreateHandler();

        var result = await handler.ProcessAsync(ValidRequest(), TestContext.Current.CancellationToken, executeInline: true);

        result.Outcome.Should().Be(TransactionIntakeOutcome.InFlight);
        _inboxStore.VerifyNoOtherCalls();
        _executionHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetStatusAsync_returns_not_found_when_no_row_exists()
    {
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);

        var handler = CreateHandler();

        var result = await handler.GetStatusAsync(TransactionId, TestContext.Current.CancellationToken);

        result.Found.Should().BeFalse();
        result.CachedResponse.Should().BeNull();
        result.StatusResponse.Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_returns_the_deserialized_cached_response_for_a_completed_row()
    {
        var cachedResponse = new TransactionResponse(TransactionId, MessageConstants.Status.Completed, _timeProvider.GetUtcNow());
        var existing = ExistingMessage(MessageConstants.Status.Completed, responsePayload: JsonSerializer.Serialize(cachedResponse));
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var handler = CreateHandler();

        var result = await handler.GetStatusAsync(TransactionId, TestContext.Current.CancellationToken);

        result.Found.Should().BeTrue();
        result.CachedResponse.Should().Be(cachedResponse);
        result.StatusResponse.Should().BeNull();
    }

    [Theory]
    [InlineData(MessageConstants.Status.Pending)]
    [InlineData(MessageConstants.Status.Processing)]
    [InlineData(MessageConstants.Status.Failed)]
    public async Task GetStatusAsync_returns_a_status_response_for_any_other_status_including_failed(string status)
    {
        var existing = ExistingMessage(status, lastError: status == MessageConstants.Status.Failed ? "boom" : null);
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var handler = CreateHandler();

        var result = await handler.GetStatusAsync(TransactionId, TestContext.Current.CancellationToken);

        result.Found.Should().BeTrue();
        result.CachedResponse.Should().BeNull();
        result.StatusResponse.Should().Be(new TransactionStatusResponse(TransactionId, status, existing.ReceivedAt, existing.ProcessedAt));
    }

    [Fact]
    public async Task GetStatusAsync_falls_through_to_a_status_response_when_a_completed_rows_payload_is_null_or_empty()
    {
        var existing = ExistingMessage(MessageConstants.Status.Completed, responsePayload: "");
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var handler = CreateHandler();

        var result = await handler.GetStatusAsync(TransactionId, TestContext.Current.CancellationToken);

        result.Found.Should().BeTrue();
        result.CachedResponse.Should().BeNull();
        result.StatusResponse.Should().Be(new TransactionStatusResponse(TransactionId, MessageConstants.Status.Completed, existing.ReceivedAt, existing.ProcessedAt));
    }

    [Fact]
    public async Task GetStatusAsync_falls_through_to_a_status_response_when_a_completed_rows_payload_is_corrupt()
    {
        var existing = ExistingMessage(MessageConstants.Status.Completed, responsePayload: "{not-valid-json");
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var handler = CreateHandler();

        var result = await handler.GetStatusAsync(TransactionId, TestContext.Current.CancellationToken);

        result.Found.Should().BeTrue();
        result.CachedResponse.Should().BeNull();
        result.StatusResponse.Should().Be(new TransactionStatusResponse(TransactionId, MessageConstants.Status.Completed, existing.ReceivedAt, existing.ProcessedAt));
    }

    [Fact]
    public async Task ProcessAsync_defers_without_claiming_when_partition_lock_is_unavailable()
    {
        _lock.Acquired = false;
        _repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxMessage?)null);
        _repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var handler = CreateHandler();

        var result = await handler.ProcessAsync(
            ValidRequest(),
            TestContext.Current.CancellationToken,
            executeInline: true);

        result.Outcome.Should().Be(TransactionIntakeOutcome.Accepted);
        _lock.LockNames.Should().ContainSingle().Which.Should().StartWith("corebank-inbox-partition-");
        _inboxStore.VerifyNoOtherCalls();
        _executionHandler.VerifyNoOtherCalls();
    }

    private InboxMessage ExistingMessage(string status, string? responsePayload = null, string? lastError = null) => new()
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
        ProcessedAt = status == MessageConstants.Status.Completed ? _timeProvider.GetUtcNow().UtcDateTime : null,
        ResponsePayload = responsePayload,
        LastError = lastError
    };

    private sealed class TestLockService : IDistributedLockService
    {
        public bool Acquired { get; set; } = true;

        /// <summary>
        /// When set, the workload still runs to completion but this reports
        /// <see langword="false"/> anyway -- reproducing
        /// <see cref="RedisDistributedLockService"/>'s documented "lock
        /// ownership was lost during the workload; not reporting success"
        /// case (as distinct from <see cref="Acquired"/> = <see langword="false"/>,
        /// where the workload never runs at all).
        /// </summary>
        public bool LoseOwnershipAfterWorkload { get; set; }

        /// <summary>When set, the call throws instead of returning -- simulating a lock backend failure (e.g. Redis connection failure).</summary>
        public Exception? ThrowException { get; set; }

        public List<string> LockNames { get; } = [];

        public async Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default)
        {
            LockNames.Add(lockName);
            if (ThrowException is not null)
            {
                throw ThrowException;
            }

            if (!Acquired)
            {
                return false;
            }

            await workload(cancellationToken);
            return !LoseOwnershipAfterWorkload;
        }
    }
}
