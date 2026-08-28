using System.Diagnostics;
using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

public sealed class PaymentStorageHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 12, 34, 56, TimeSpan.Zero);

    private readonly Mock<IOutboxRepository> _repository = new(MockBehavior.Strict);
    private readonly FixedTimeProvider _timeProvider = new(Now);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t exact Key \r\n")]
    public async Task StoreAsync_preserves_every_non_null_key_verbatim(string key)
    {
        OutboxMessage? stored = null;
        _repository.Setup(repository => repository.StoreIfNewAsync(
                It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((message, _) => stored = message)
            .ReturnsAsync(true);
        var handler = CreateHandler();

        var result = await handler.StoreAsync(
            ValidRequest(), key, TestContext.Current.CancellationToken);

        result.IsNew.Should().BeTrue();
        result.Payment.Should().BeSameAs(stored);
        stored!.IdempotencyKey.Should().Be(key);
        stored.TransactionId.Should().Be(key);
        stored.PartitionId.Should().Be(PartitionHelper.GetPartitionId(key, 4));
        AssertPaymentFields(stored);
        _repository.Verify(repository => repository.StoreIfNewAsync(
            It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StoreAsync_generates_a_canonical_guid_when_the_key_is_null()
    {
        OutboxMessage? stored = null;
        _repository.Setup(repository => repository.StoreIfNewAsync(
                It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((message, _) => stored = message)
            .ReturnsAsync(true);
        var handler = CreateHandler();

        await handler.StoreAsync(ValidRequest(), null, TestContext.Current.CancellationToken);

        Guid.TryParseExact(stored!.IdempotencyKey, "D", out _).Should().BeTrue();
        stored.TransactionId.Should().Be(stored.IdempotencyKey);
        stored.PartitionId.Should().Be(PartitionHelper.GetPartitionId(stored.IdempotencyKey, 4));
    }

    [Fact]
    public async Task StoreAsync_captures_the_current_activity_trace_context()
    {
        using var listener = ListenToAllActivities();
        using var source = new ActivitySource(nameof(PaymentStorageHandlerTests));
        using var activity = source.StartActivity(
            "store",
            ActivityKind.Internal,
            ActivityContext.Parse(
                "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
                "vendor=value"));
        OutboxMessage? stored = null;
        _repository.Setup(repository => repository.StoreIfNewAsync(
                It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((message, _) => stored = message)
            .ReturnsAsync(true);

        await CreateHandler().StoreAsync(
            ValidRequest(), "trace-key", TestContext.Current.CancellationToken);

        stored!.TraceParent.Should().Be(activity!.Id);
        stored.TraceState.Should().Be("vendor=value");
    }

    [Fact]
    public async Task StoreAsync_persists_null_trace_fields_without_an_ambient_activity()
    {
        Activity.Current.Should().BeNull();
        OutboxMessage? stored = null;
        _repository.Setup(repository => repository.StoreIfNewAsync(
                It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((message, _) => stored = message)
            .ReturnsAsync(true);

        await CreateHandler().StoreAsync(
            ValidRequest(), "no-trace", TestContext.Current.CancellationToken);

        stored!.TraceParent.Should().BeNull();
        stored.TraceState.Should().BeNull();
    }

    [Fact]
    public async Task StoreAsync_returns_the_persisted_winner_after_losing_a_duplicate_race()
    {
        var winner = NewPersistedMessage("race-key");
        OutboxMessage? candidate = null;
        _repository.Setup(repository => repository.StoreIfNewAsync(
                It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((message, _) => candidate = message)
            .ReturnsAsync(false);
        _repository.Setup(repository => repository.FindByIdempotencyKeyAsync(
                "race-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(winner);

        var result = await CreateHandler().StoreAsync(
            ValidRequest(), "race-key", TestContext.Current.CancellationToken);

        result.IsNew.Should().BeFalse();
        result.Payment.Should().BeSameAs(winner);
        result.Payment.Should().NotBeSameAs(candidate);
        _repository.VerifyAll();
    }

    [Fact]
    public async Task Concurrent_duplicate_storage_returns_one_persisted_winner_to_both_callers()
    {
        var repository = new CoordinatedRepository();
        var options = Options.Create(new OutboxProcessingOptions
        {
            PartitionCount = 4,
            LockExpirySeconds = 30,
            PollingIntervalMs = 5_000
        });
        var firstHandler = new PaymentStorageHandler(repository, options, _timeProvider);
        var secondHandler = new PaymentStorageHandler(repository, options, _timeProvider);

        var results = await Task.WhenAll(
            firstHandler.StoreAsync(ValidRequest(), "concurrent-key", TestContext.Current.CancellationToken),
            secondHandler.StoreAsync(ValidRequest(), "concurrent-key", TestContext.Current.CancellationToken));

        results.Count(result => result.IsNew).Should().Be(1);
        results.Count(result => !result.IsNew).Should().Be(1);
        results[0].Payment.Should().BeSameAs(results[1].Payment);
        repository.StoredMessages.Should().ContainSingle()
            .Which.Should().BeSameAs(results[0].Payment);
    }

    [Fact]
    public async Task StoreAsync_throws_an_explicit_invalid_state_when_the_winner_is_missing()
    {
        _repository.Setup(repository => repository.StoreIfNewAsync(
                It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _repository.Setup(repository => repository.FindByIdempotencyKeyAsync(
                "missing-winner", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OutboxMessage?)null);

        var act = async () => await CreateHandler().StoreAsync(
            ValidRequest(), "missing-winner", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*persisted winner was not found*");
    }

    [Fact]
    public async Task StoreAsync_propagates_infrastructure_failures()
    {
        var failure = new InvalidOperationException("database unavailable");
        _repository.Setup(repository => repository.StoreIfNewAsync(
                It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var act = async () => await CreateHandler().StoreAsync(
            ValidRequest(), "failure-key", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task StoreAsync_rejects_a_null_request()
    {
        var act = async () => await CreateHandler().StoreAsync(
            null!, "key", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
        _repository.VerifyNoOtherCalls();
    }

    private PaymentStorageHandler CreateHandler() =>
        new(
            _repository.Object,
            Options.Create(new OutboxProcessingOptions
            {
                PartitionCount = 4,
                LockExpirySeconds = 30,
                PollingIntervalMs = 5_000
            }),
            _timeProvider);

    private static PaymentRequest ValidRequest() =>
        new("NL91ABNA0417164300", "NL20INGB0001234567", 42.50m, "EUR");

    private static OutboxMessage NewPersistedMessage(string key) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = key,
        TransactionId = key,
        FromAccount = "NL91ABNA0417164300",
        ToAccount = "NL20INGB0001234567",
        Amount = 42.50m,
        Currency = "EUR",
        PartitionId = PartitionHelper.GetPartitionId(key, 4),
        CreatedAt = Now.UtcDateTime,
        Status = MessageConstants.Status.Pending
    };

    private static void AssertPaymentFields(OutboxMessage message)
    {
        message.Id.Should().NotBe(Guid.Empty);
        message.FromAccount.Should().Be("NL91ABNA0417164300");
        message.ToAccount.Should().Be("NL20INGB0001234567");
        message.Amount.Should().Be(42.50m);
        message.Currency.Should().Be("EUR");
        message.Status.Should().Be(MessageConstants.Status.Pending);
        message.RetryCount.Should().Be(0);
        message.ProcessedAt.Should().BeNull();
        message.LastError.Should().BeNull();
        message.CreatedAt.Should().Be(Now.UtcDateTime);
    }

    private static ActivityListener ListenToAllActivities()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CoordinatedRepository : IOutboxRepository
    {
        private readonly TaskCompletionSource _bothCandidatesReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();
        private int _candidateCount;

        public IReadOnlyCollection<OutboxMessage> StoredMessages
        {
            get
            {
                lock (_sync)
                {
                    return _winner is null ? [] : [_winner];
                }
            }
        }

        private OutboxMessage? _winner;

        public async Task<bool> StoreIfNewAsync(
            OutboxMessage message,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _candidateCount) == 2)
            {
                _bothCandidatesReady.SetResult();
            }

            await _bothCandidatesReady.Task.WaitAsync(cancellationToken);

            lock (_sync)
            {
                if (_winner is not null)
                {
                    return false;
                }

                _winner = message;
                return true;
            }
        }

        public Task<OutboxMessage?> FindByIdempotencyKeyAsync(
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                return Task.FromResult(
                    _winner?.IdempotencyKey == idempotencyKey ? _winner : null);
            }
        }
    }
}
