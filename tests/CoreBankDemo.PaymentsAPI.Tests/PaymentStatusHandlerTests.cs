using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using Moq;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// The spec's status matrix: the reported Status is CoreBank's committed
/// business outcome, never the outbox row's transport Status on its own.
/// </summary>
public class PaymentStatusHandlerTests
{
    private const string Key = "payment-key";
    private static readonly DateTime CreatedAt = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset CreatedAtOffset = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private const string SettledAtJson = "2026-10-07T12:00:05+00:00";
    private static readonly DateTimeOffset SettledAt = new(2026, 10, 7, 12, 0, 5, TimeSpan.Zero);

    private readonly Mock<IOutboxRepository> _repository = new(MockBehavior.Strict);

    private static OutboxMessage Row(string status, string? payload = null, DateTime? processedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = Key,
        TransactionId = Key,
        FromAccount = "NL91ABNA0417164300",
        ToAccount = "NL20INGB0001234567",
        Amount = 12.34m,
        Currency = "EUR",
        PartitionId = 1,
        Status = status,
        CreatedAt = CreatedAt,
        ProcessedAt = processedAt,
        ResponsePayload = payload
    };

    private static string Payload(string status) =>
        $$"""{"TransactionId":"{{Key}}","Status":"{{status}}","ProcessedAt":"{{SettledAtJson}}"}""";

    private async Task<PaymentResponse?> GetAsync(OutboxMessage? row)
    {
        _repository
            .Setup(repository => repository.FindByIdempotencyKeyAsync(Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);
        return await new PaymentStatusHandler(_repository.Object)
            .GetAsync(Key, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(MessageConstants.Status.Completed)]
    [InlineData(MessageConstants.Status.Failed)]
    [InlineData(MessageConstants.Status.Cancelled)]
    public async Task Row_1_a_committed_cached_outcome_is_reported_with_its_time(string outcome)
    {
        var response = await GetAsync(Row(MessageConstants.Status.Completed, Payload(outcome)));

        response.Should().NotBeNull();
        response!.Status.Should().Be(outcome);
        response.ProcessedAt.Should().Be(SettledAt);
    }

    [Theory]
    [InlineData(MessageConstants.Status.Pending)]
    [InlineData(MessageConstants.Status.Processing)]
    public async Task Row_1_a_cached_cancellation_wins_over_a_not_yet_delivered_row(string rowStatus)
    {
        // spec: instant-rail-cancelled-event -- CoreBank's cancellation can be
        // cached while the row is still waiting for the background rail.
        var response = await GetAsync(Row(rowStatus, Payload(MessageConstants.Status.Cancelled)));

        response!.Status.Should().Be(MessageConstants.Status.Cancelled);
        response.ProcessedAt.Should().Be(SettledAt);
    }

    [Fact]
    public async Task Row_2_a_cancelled_row_without_a_cached_outcome_uses_its_processed_time()
    {
        var cancelledAt = new DateTime(2026, 10, 7, 12, 0, 9, DateTimeKind.Utc);

        var response = await GetAsync(Row(MessageConstants.Status.Cancelled, processedAt: cancelledAt));

        response!.Status.Should().Be(MessageConstants.Status.Cancelled);
        response.ProcessedAt.Should().Be(new DateTimeOffset(cancelledAt));
    }

    [Fact]
    public async Task Row_2_a_cancelled_row_without_any_time_falls_back_to_creation()
    {
        var response = await GetAsync(Row(MessageConstants.Status.Cancelled));

        response!.Status.Should().Be(MessageConstants.Status.Cancelled);
        response.ProcessedAt.Should().Be(CreatedAtOffset);
    }

    [Fact]
    public async Task Row_3_a_legacy_failed_row_reports_failed()
    {
        var response = await GetAsync(Row(MessageConstants.Status.Failed));

        response!.Status.Should().Be(MessageConstants.Status.Failed);
        response.ProcessedAt.Should().Be(CreatedAtOffset);
    }

    [Theory]
    [InlineData(MessageConstants.Status.Pending, null)]
    [InlineData(MessageConstants.Status.Processing, null)]
    // Technical Completed = delivered to CoreBank's inbox, not executed.
    [InlineData(MessageConstants.Status.Completed, null)]
    [InlineData(MessageConstants.Status.Completed, """{"TransactionId":"payment-key","Status":"Pending","ProcessedAt":"2026-10-07T12:00:05+00:00"}""")]
    [InlineData(MessageConstants.Status.Completed, "not json")]
    [InlineData(MessageConstants.Status.Completed, """{"TransactionId":"payment-key","Status":"","ProcessedAt":"2026-10-07T12:00:05+00:00"}""")]
    public async Task Row_4_anything_without_a_committed_outcome_is_pending(string rowStatus, string? payload)
    {
        var response = await GetAsync(Row(rowStatus, payload));

        response!.Status.Should().Be(MessageConstants.Status.Pending);
        response.ProcessedAt.Should().Be(CreatedAtOffset);
    }

    [Fact]
    public async Task The_response_carries_the_row_identity_and_amount()
    {
        var response = await GetAsync(Row(MessageConstants.Status.Pending));

        response.Should().Be(new PaymentResponse(
            Key, Key, MessageConstants.Status.Pending, 12.34m, "EUR", CreatedAtOffset));
    }

    [Fact]
    public async Task An_unknown_id_is_not_found()
    {
        (await GetAsync(null)).Should().BeNull();
    }

    [Fact]
    public async Task An_id_longer_than_the_column_is_not_found_without_a_query()
    {
        var tooLong = new string('a', PaymentStatusHandler.MaxTransactionIdLength + 1);

        var response = await new PaymentStatusHandler(_repository.Object)
            .GetAsync(tooLong, TestContext.Current.CancellationToken);

        response.Should().BeNull();
        _repository.VerifyNoOtherCalls();
    }
}
