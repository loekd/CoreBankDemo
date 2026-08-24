using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.CloudEventTypes;

/// <summary>
/// Story 3.3 (AD-12): proves the three CloudEvent records serialize
/// byte-for-byte to the frozen legacy JSON shape (property names, order, and
/// casing) against fixed known-good JSON strings.
/// <para>
/// Serialization uses the same <see cref="JsonSerializerOptions"/> Dapr's own
/// <c>DaprClientBuilder</c> defaults to — <c>PropertyNamingPolicy =
/// JsonNamingPolicy.CamelCase</c> — since that is what actually runs when
/// <c>DaprEventPublisher</c> hands these payloads to
/// <c>DaprClient.PublishEventAsync</c> in production. Confirmed by reflecting
/// over a freshly-built <c>DaprClient</c>'s <c>JsonSerializerOptions</c>
/// property (Dapr.Client 1.17.9): <c>PropertyNamingPolicy</c> is
/// <c>JsonCamelCaseNamingPolicy</c> and <c>DefaultIgnoreCondition</c> is
/// <c>Never</c> (nulls are emitted, not omitted) — both reproduced here.
/// </para>
/// </summary>
public class CloudEventJsonSnapshotTests
{
    private static readonly JsonSerializerOptions DaprDefaults = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly DateTimeOffset FixedProcessedAt =
        new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BalanceUpdatedEvent_serializes_to_the_exact_legacy_JSON_shape()
    {
        var evt = new BalanceUpdatedEvent(
            TransactionId: "tx-1",
            AccountNumber: "ACC-123",
            Delta: 100.50m,
            NewBalance: 500.75m,
            Currency: "USD");

        var json = JsonSerializer.Serialize(evt, DaprDefaults);

        json.Should().Be(
            "{\"transactionId\":\"tx-1\",\"accountNumber\":\"ACC-123\",\"delta\":100.50,\"newBalance\":500.75,\"currency\":\"USD\"}");
    }

    [Fact]
    public void TransactionCompletedEvent_serializes_to_the_exact_legacy_JSON_shape()
    {
        var evt = new TransactionCompletedEvent(
            TransactionId: "tx-1",
            Status: "Completed",
            ProcessedAt: FixedProcessedAt);

        var json = JsonSerializer.Serialize(evt, DaprDefaults);

        json.Should().Be(
            "{\"transactionId\":\"tx-1\",\"status\":\"Completed\",\"processedAt\":\"2026-08-24T12:00:00+00:00\"}");
    }

    [Fact]
    public void TransactionFailedEvent_serializes_to_the_exact_legacy_JSON_shape_when_ErrorReason_is_present()
    {
        var evt = new TransactionFailedEvent(
            TransactionId: "tx-1",
            Status: "Failed",
            ProcessedAt: FixedProcessedAt,
            ErrorReason: "insufficient funds");

        var json = JsonSerializer.Serialize(evt, DaprDefaults);

        json.Should().Be(
            "{\"transactionId\":\"tx-1\",\"status\":\"Failed\",\"processedAt\":\"2026-08-24T12:00:00+00:00\",\"errorReason\":\"insufficient funds\"}");
    }

    [Fact]
    public void TransactionFailedEvent_serializes_ErrorReason_as_JSON_null_when_the_value_is_null_not_omitted()
    {
        var evt = new TransactionFailedEvent(
            TransactionId: "tx-1",
            Status: "Failed",
            ProcessedAt: FixedProcessedAt,
            ErrorReason: null);

        var json = JsonSerializer.Serialize(evt, DaprDefaults);

        json.Should().Be(
            "{\"transactionId\":\"tx-1\",\"status\":\"Failed\",\"processedAt\":\"2026-08-24T12:00:00+00:00\",\"errorReason\":null}");
    }
}
