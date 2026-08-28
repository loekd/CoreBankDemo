using System.Diagnostics;
using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

public class PaymentStorageHandlerTests
{
    private static readonly PaymentRequest Request =
        new("NL91ABNA0417164300", "NL20INGB0001234567", 12.34m, "EUR");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 12, 34, 56, TimeSpan.Zero);

    [Theory]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Invalid_caller_key_returns_validation_without_repository_call(string key)
    {
        var repository = new Mock<IOutboxRepository>(MockBehavior.Strict);
        var handler = CreateHandler(repository.Object);

        var result = await handler.StoreAsync(Request, key, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(PaymentStorageOutcome.ValidationFailed);
        result.Payment.Should().BeNull();
        result.Errors.Should().ContainSingle().Which.Should().Contain("1 and 100");
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("x")]
    [InlineData("   ")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Caller_key_is_preserved_verbatim_and_used_for_identity_and_partition(string key)
    {
        OutboxMessage? captured = null;
        var repository = new Mock<IOutboxRepository>();
        repository
            .Setup(store => store.StoreIfNewAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((message, _) => captured = message)
            .ReturnsAsync(true);
        var handler = CreateHandler(repository.Object);

        var result = await handler.StoreAsync(Request, key, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(PaymentStorageOutcome.Stored);
        captured.Should().NotBeNull();
        captured!.IdempotencyKey.Should().Be(key);
        captured.TransactionId.Should().Be(key);
        captured.PartitionId.Should().Be(PartitionHelper.GetPartitionId(key, 4));
        result.Payment!.IdempotencyKey.Should().Be(key);
    }

    [Fact]
    public async Task Null_key_generates_canonical_guid_and_captures_time_status_and_no_trace()
    {
        OutboxMessage? captured = null;
        var repository = new Mock<IOutboxRepository>();
        repository
            .Setup(store => store.StoreIfNewAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((message, _) => captured = message)
            .ReturnsAsync(true);
        var handler = CreateHandler(repository.Object);

        var result = await handler.StoreAsync(Request, null, TestContext.Current.CancellationToken);

        Guid.TryParseExact(captured!.IdempotencyKey, "D", out _).Should().BeTrue();
        captured.TransactionId.Should().Be(captured.IdempotencyKey);
        captured.CreatedAt.Should().Be(Now.UtcDateTime);
        captured.Status.Should().Be(MessageConstants.Status.Pending);
        captured.TraceParent.Should().BeNull();
        captured.TraceState.Should().BeNull();
        result.Payment.Should().BeEquivalentTo(new
        {
            captured.Id,
            captured.IdempotencyKey,
            captured.TransactionId,
            captured.FromAccount,
            captured.ToAccount,
            captured.Amount,
            captured.Currency,
            captured.PartitionId,
            captured.Status,
            captured.CreatedAt,
            captured.TraceParent,
            captured.TraceState
        });
    }

    [Fact]
    public async Task Ambient_activity_trace_context_is_persisted()
    {
        OutboxMessage? captured = null;
        var repository = new Mock<IOutboxRepository>();
        repository
            .Setup(store => store.StoreIfNewAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((message, _) => captured = message)
            .ReturnsAsync(true);
        var handler = CreateHandler(repository.Object);
        using var activity = new Activity("store-payment").SetIdFormat(ActivityIdFormat.W3C);
        activity.TraceStateString = "vendor=value";
        activity.Start();

        await handler.StoreAsync(Request, "trace-key", TestContext.Current.CancellationToken);

        captured!.TraceParent.Should().Be(activity.Id);
        captured.TraceState.Should().Be("vendor=value");
    }

    [Fact]
    public async Task Duplicate_returns_snapshot_of_persisted_winner()
    {
        var candidateId = Guid.Empty;
        var winner = PaymentsApiTestData.Outbox("duplicate-key");
        winner.Amount = 99m;
        var repository = new Mock<IOutboxRepository>();
        repository
            .Setup(store => store.StoreIfNewAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((message, _) => candidateId = message.Id)
            .ReturnsAsync(false);
        repository
            .Setup(store => store.FindByIdempotencyKeyAsync("duplicate-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(winner);
        var handler = CreateHandler(repository.Object);

        var result = await handler.StoreAsync(Request, "duplicate-key", TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(PaymentStorageOutcome.Duplicate);
        result.Payment!.Id.Should().Be(winner.Id).And.NotBe(candidateId);
        result.Payment.Amount.Should().Be(99m);
    }

    [Fact]
    public async Task Missing_winner_after_race_throws_explicit_invalid_state()
    {
        var repository = new Mock<IOutboxRepository>();
        repository
            .Setup(store => store.StoreIfNewAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository
            .Setup(store => store.FindByIdempotencyKeyAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OutboxMessage?)null);
        var handler = CreateHandler(repository.Object);

        var act = () => handler.StoreAsync(Request, "missing", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no persisted winner*");
    }

    [Fact]
    public async Task Infrastructure_errors_propagate()
    {
        var expected = new InvalidOperationException("database unavailable");
        var repository = new Mock<IOutboxRepository>();
        repository
            .Setup(store => store.StoreIfNewAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expected);
        var handler = CreateHandler(repository.Object);

        var act = () => handler.StoreAsync(Request, "failure", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Concurrent_handlers_return_the_same_persisted_winner()
    {
        await using var store = new SqlitePaymentsStore();
        await using var firstContext = store.CreateContext();
        await using var secondContext = store.CreateContext();
        var first = CreateHandler(new OutboxRepository(firstContext, TimeProvider.System));
        var second = CreateHandler(new OutboxRepository(secondContext, TimeProvider.System));

        var results = await Task.WhenAll(
            first.StoreAsync(Request, "handler-race", TestContext.Current.CancellationToken),
            second.StoreAsync(Request, "handler-race", TestContext.Current.CancellationToken));

        results.Select(result => result.Payment!.Id).Distinct().Should().ContainSingle();
        results.Select(result => result.Outcome)
            .Should().BeEquivalentTo([PaymentStorageOutcome.Stored, PaymentStorageOutcome.Duplicate]);
        await using var verification = store.CreateContext();
        verification.OutboxMessages.Count(message => message.IdempotencyKey == "handler-race").Should().Be(1);
    }

    [Fact]
    public async Task Null_request_is_rejected()
    {
        var handler = CreateHandler(Mock.Of<IOutboxRepository>());
        var act = () => handler.StoreAsync(null!, "key", TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static PaymentStorageHandler CreateHandler(IOutboxRepository repository) =>
        new(
            repository,
            Options.Create(new OutboxProcessingOptions
            {
                PartitionCount = 4,
                LockExpirySeconds = 30,
                PollingIntervalMs = 200
            }),
            new FixedTimeProvider(Now),
            NullLogger<PaymentStorageHandler>.Instance);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
