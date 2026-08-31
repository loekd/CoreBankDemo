using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using CoreBankDemo.ServiceDefaults.Tests.Metrics;
using Dapr.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.EventPublisher;

/// <summary>
/// Story 3.3: <see cref="DaprEventPublisher"/> against a mocked
/// <see cref="DaprClient"/>. <c>DaprClient.PublishEventAsync&lt;TData&gt;</c>
/// is abstract and virtual (confirmed by reflecting over Dapr.Client 1.17.9),
/// so it is directly Moq-mockable — same no-wrapper-seam pattern story 3.2
/// validated for <c>Lock</c>/<c>Unlock</c>. <see cref="DaprEventPublisher"/>
/// calls the <c>object</c>-typed overload (its own <c>payload</c> parameter
/// is statically typed <c>object</c>, so <c>TData</c> is always inferred as
/// <c>object</c> at the call site) — tests set up and verify against
/// <c>PublishEventAsync&lt;object&gt;</c> explicitly for that reason.
/// Pub/sub and topic names come from <see cref="MessagingOutboxProcessingOptions"/>
/// (story 3.1), never as per-call parameters.
/// </summary>
public class DaprEventPublisherTests
{
    private static (Mock<DaprClient> DaprClient, Mock<ILogger<DaprEventPublisher>> Logger, DaprEventPublisher Sut, BusinessMetrics BusinessMetrics) CreateSut(
        string pubSubName = "pubsub", string topicName = "transaction-events")
    {
        var daprClient = new Mock<DaprClient>();
        var logger = new Mock<ILogger<DaprEventPublisher>>();
        var options = Options.Create(new MessagingOutboxProcessingOptions
        {
            PartitionCount = 4,
            LockExpirySeconds = 30,
            PollingIntervalMs = 5_000,
            PubSubName = pubSubName,
            TopicName = topicName,
        });
        var businessMetrics = new BusinessMetrics();
        var sut = new DaprEventPublisher(daprClient.Object, options, logger.Object, businessMetrics);
        return (daprClient, logger, sut, businessMetrics);
    }

    [Fact]
    public async Task PublishAsync_calls_Dapr_PublishEventAsync_with_the_DI_bound_pubsub_and_topic_and_the_payload_unchanged()
    {
        var (daprClient, _, sut, _) = CreateSut(pubSubName: "custom-pubsub", topicName: "custom-topic");
        var payload = new { TransactionId = "tx-1" };
        string? capturedPubSub = null;
        string? capturedTopic = null;
        object? capturedPayload = null;
        daprClient
            .Setup(c => c.PublishEventAsync<object>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, object, Dictionary<string, string>, CancellationToken>((pubsub, topic, data, _, _) =>
            {
                capturedPubSub = pubsub;
                capturedTopic = topic;
                capturedPayload = data;
            })
            .Returns(Task.CompletedTask);

        await sut.PublishAsync("com.corebank.transaction.completed", "corebank-api", "tx-1", payload, traceParent: null, traceState: null,
            TestContext.Current.CancellationToken);

        capturedPubSub.Should().Be("custom-pubsub");
        capturedTopic.Should().Be("custom-topic");
        capturedPayload.Should().BeSameAs(payload);
    }

    [Fact]
    public async Task PublishAsync_always_includes_cloudevent_type_source_and_subject_in_metadata()
    {
        var (daprClient, _, sut, _) = CreateSut();
        Dictionary<string, string>? capturedMetadata = null;
        daprClient
            .Setup(c => c.PublishEventAsync<object>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, object, Dictionary<string, string>, CancellationToken>((_, _, _, metadata, _) => capturedMetadata = metadata)
            .Returns(Task.CompletedTask);

        await sut.PublishAsync("com.corebank.transaction.completed", "corebank-api", "tx-42", new { }, traceParent: null, traceState: null,
            TestContext.Current.CancellationToken);

        capturedMetadata.Should().NotBeNull();
        capturedMetadata!["cloudevent.type"].Should().Be("com.corebank.transaction.completed");
        capturedMetadata["cloudevent.source"].Should().Be("corebank-api");
        capturedMetadata["cloudevent.subject"].Should().Be("tx-42");
    }

    [Fact]
    public async Task PublishAsync_includes_cloudevent_traceparent_when_traceParent_is_supplied()
    {
        var (daprClient, _, sut, _) = CreateSut();
        Dictionary<string, string>? capturedMetadata = null;
        daprClient
            .Setup(c => c.PublishEventAsync<object>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, object, Dictionary<string, string>, CancellationToken>((_, _, _, metadata, _) => capturedMetadata = metadata)
            .Returns(Task.CompletedTask);

        await sut.PublishAsync("com.corebank.transaction.completed", "corebank-api", "tx-42", new { },
            traceParent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            traceState: null,
            cancellationToken: TestContext.Current.CancellationToken);

        capturedMetadata!["cloudevent.traceparent"].Should().Be("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PublishAsync_omits_cloudevent_traceparent_entirely_when_traceParent_is_null_or_whitespace(string? traceParent)
    {
        var (daprClient, _, sut, _) = CreateSut();
        Dictionary<string, string>? capturedMetadata = null;
        daprClient
            .Setup(c => c.PublishEventAsync<object>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, object, Dictionary<string, string>, CancellationToken>((_, _, _, metadata, _) => capturedMetadata = metadata)
            .Returns(Task.CompletedTask);

        await sut.PublishAsync("com.corebank.transaction.completed", "corebank-api", "tx-42", new { }, traceParent, null,
            TestContext.Current.CancellationToken);

        capturedMetadata.Should().NotBeNull();
        capturedMetadata!.Should().NotContainKey("cloudevent.traceparent");
    }

    [Fact]
    public async Task PublishAsync_propagates_the_cancellation_token_to_Dapr_unchanged()
    {
        var (daprClient, _, sut, _) = CreateSut();
        using var cts = new CancellationTokenSource();
        CancellationToken? capturedToken = null;
        daprClient
            .Setup(c => c.PublishEventAsync<object>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, object, Dictionary<string, string>, CancellationToken>((_, _, _, _, ct) => capturedToken = ct)
            .Returns(Task.CompletedTask);

        await sut.PublishAsync("com.corebank.transaction.completed", "corebank-api", "tx-42", new { }, null, null, cts.Token);

        capturedToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task PublishAsync_does_not_catch_exceptions_from_Dapr_PublishEventAsync_they_propagate_to_the_caller()
    {
        var (daprClient, _, sut, _) = CreateSut();
        daprClient
            .Setup(c => c.PublishEventAsync<object>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("pubsub unreachable"));

        var act = async () => await sut.PublishAsync("com.corebank.transaction.completed", "corebank-api", "tx-42", new { }, null, null,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("pubsub unreachable");
    }

    // ---- Story 6.5: business metrics ----

    [Fact]
    public async Task PublishAsync_records_a_succeeded_sent_dapr_delivery_metric_after_publish_completes()
    {
        var (daprClient, _, sut, businessMetrics) = CreateSut();
        using var listener = new MetricsTestListener(businessMetrics);
        daprClient
            .Setup(c => c.PublishEventAsync<object>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await sut.PublishAsync("com.corebank.transaction.completed", "corebank-api", "tx-42", new { }, null, null,
            TestContext.Current.CancellationToken);

        var measurement = listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.MessagingDeliveriesInstrumentName).Which;
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["messaging.direction"] = "sent",
            ["messaging.transport"] = "dapr",
            ["messaging.message.type"] = "transaction-completed",
            ["outcome"] = "succeeded",
        });
    }

    [Theory]
    [InlineData("com.corebank.transaction.failed", "transaction-failed")]
    [InlineData("com.corebank.account.balance.updated", "balance-updated")]
    [InlineData("com.corebank.unrecognized", "unknown")]
    public async Task PublishAsync_maps_the_outgoing_cloud_event_type_to_the_closed_message_type_vocabulary(
        string cloudEventType, string expectedMessageType)
    {
        var (daprClient, _, sut, businessMetrics) = CreateSut();
        using var listener = new MetricsTestListener(businessMetrics);
        daprClient
            .Setup(c => c.PublishEventAsync<object>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await sut.PublishAsync(cloudEventType, "corebank-api", "tx-42", new { }, null, null, TestContext.Current.CancellationToken);

        listener.Measurements.Should().ContainSingle(m => m.InstrumentName == BusinessMetrics.MessagingDeliveriesInstrumentName)
            .Which.Tags["messaging.message.type"].Should().Be(expectedMessageType);
    }

    [Fact]
    public async Task PublishAsync_records_a_failed_sent_dapr_delivery_metric_and_still_rethrows_when_PublishEventAsync_throws()
    {
        var (daprClient, _, sut, businessMetrics) = CreateSut();
        using var listener = new MetricsTestListener(businessMetrics);
        daprClient
            .Setup(c => c.PublishEventAsync<object>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("pubsub unreachable"));

        var act = async () => await sut.PublishAsync("com.corebank.transaction.completed", "corebank-api", "tx-42", new { }, null, null,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("pubsub unreachable");
        listener.Measurements.Should().ContainSingle(m => m.InstrumentName == BusinessMetrics.MessagingDeliveriesInstrumentName)
            .Which.Tags["outcome"].Should().Be("failed");
    }

    [Fact]
    public async Task PublishAsync_records_no_failed_delivery_when_the_caller_cancels()
    {
        var (daprClient, _, sut, businessMetrics) = CreateSut();
        using var listener = new MetricsTestListener(businessMetrics);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        daprClient
            .Setup(c => c.PublishEventAsync<object>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>>(), cts.Token))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = () => sut.PublishAsync(
            "com.corebank.transaction.completed", "corebank-api", "tx-42", new { }, null, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        listener.Measurements.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_includes_cloudevent_tracestate_when_supplied()
    {
        var (daprClient, _, sut, _) = CreateSut();
        Dictionary<string, string>? capturedMetadata = null;
        daprClient
            .Setup(c => c.PublishEventAsync<object>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, object, Dictionary<string, string>, CancellationToken>((_, _, _, metadata, _) => capturedMetadata = metadata)
            .Returns(Task.CompletedTask);

        await sut.PublishAsync("com.corebank.transaction.completed", "corebank-api", "tx-42", new { },
            traceParent: null, traceState: "vendor=value", cancellationToken: TestContext.Current.CancellationToken);

        capturedMetadata!["cloudevent.tracestate"].Should().Be("vendor=value");
    }
}
