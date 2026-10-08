using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Outbox;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

public class CachedTransactionOutcomeTests
{
    [Fact]
    public void TryRead_returns_the_cached_submission()
    {
        var cached = CachedTransactionOutcome.TryRead(
            """{"TransactionId":"txn-1","Status":"Completed","ProcessedAt":"2026-10-07T12:00:05+00:00"}""");

        cached.Should().NotBeNull();
        cached!.TransactionId.Should().Be("txn-1");
        cached.Status.Should().Be(MessageConstants.Status.Completed);
        cached.ProcessedAt.Should().Be(new DateTimeOffset(2026, 10, 7, 12, 0, 5, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"TransactionId":"txn-1","ProcessedAt":"2026-10-07T12:00:05+00:00"}""")]
    [InlineData("""{"TransactionId":"txn-1","Status":"  ","ProcessedAt":"2026-10-07T12:00:05+00:00"}""")]
    public void TryRead_returns_null_for_a_missing_corrupt_or_status_less_payload(string? payload)
    {
        CachedTransactionOutcome.TryRead(payload).Should().BeNull();
    }

    [Theory]
    [InlineData(MessageConstants.Status.Completed, true)]
    [InlineData(MessageConstants.Status.Failed, true)]
    [InlineData(MessageConstants.Status.Cancelled, true)]
    [InlineData(MessageConstants.Status.Pending, false)]
    [InlineData(MessageConstants.Status.Processing, false)]
    public void IsCommitted_is_true_only_for_terminal_business_outcomes(string status, bool expected)
    {
        CachedTransactionOutcome.IsCommitted(status).Should().Be(expected);
    }
}
