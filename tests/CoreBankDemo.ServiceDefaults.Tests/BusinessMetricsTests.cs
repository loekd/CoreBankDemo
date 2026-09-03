using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults.Tests.Metrics;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests;

/// <summary>
/// Story 6.5: proves <see cref="BusinessMetrics"/>'s instrument metadata
/// (name/unit/description) and that every typed recording method emits
/// exactly one measurement carrying only its closed-set attributes — never a
/// free-form tag. Uses a real <see cref="System.Diagnostics.Metrics.MeterListener"/>
/// (<see cref="MetricsTestListener"/>) rather than mocking anything, since
/// <see cref="BusinessMetrics"/> owns its own <c>Meter</c>/instruments end to
/// end (design notes: "the shared recorder should own Meter and instrument
/// lifetime").
/// </summary>
public class BusinessMetricsTests
{
    [Fact]
    public void Every_instrument_is_published_under_the_shared_meter_with_its_documented_metadata()
    {
        // Own BusinessMetrics instance, filtered by Meter reference (not just
        // MeterName): parallel test execution means other tests' own
        // BusinessMetrics instances publish instruments under the same name
        // concurrently, and name-only filtering would leak duplicate
        // instrument-published events into this test's `published` list.
        using var metrics = new BusinessMetrics();
        var published = new List<(string Name, string? Unit, string? Description)>();
        using var rawListener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.Meter))
                {
                    published.Add((instrument.Name, instrument.Unit, instrument.Description));
                }
            }
        };
        // MeterListener.Start() replays InstrumentPublished for every
        // already-published instrument on every meter it observes, so
        // instruments created by the BusinessMetrics constructor above are
        // still captured even though Start() runs after construction here.
        rawListener.Start();

        published.Select(p => p.Name).Should().BeEquivalentTo(
        [
            BusinessMetrics.PaymentIntakeInstrumentName,
            BusinessMetrics.TransactionIntakeInstrumentName,
            BusinessMetrics.TransactionProcessedInstrumentName,
            BusinessMetrics.MessagingStoreOperationsInstrumentName,
            BusinessMetrics.MessagingItemsProcessedInstrumentName,
            BusinessMetrics.MessagingQueueDurationInstrumentName,
            BusinessMetrics.MessagingDeliveriesInstrumentName,
            BusinessMetrics.PaymentInstantDurationInstrumentName,
        ]);

        published.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Unit));
        published.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Description));

        published.Single(p => p.Name == BusinessMetrics.PaymentIntakeInstrumentName).Unit.Should().Be("{payment}");
        published.Single(p => p.Name == BusinessMetrics.TransactionIntakeInstrumentName).Unit.Should().Be("{transaction}");
        published.Single(p => p.Name == BusinessMetrics.TransactionProcessedInstrumentName).Unit.Should().Be("{transaction}");
        published.Single(p => p.Name == BusinessMetrics.MessagingStoreOperationsInstrumentName).Unit.Should().Be("{operation}");
        published.Single(p => p.Name == BusinessMetrics.MessagingItemsProcessedInstrumentName).Unit.Should().Be("{item}");
        published.Single(p => p.Name == BusinessMetrics.MessagingQueueDurationInstrumentName).Unit.Should().Be("ms");
        published.Single(p => p.Name == BusinessMetrics.MessagingDeliveriesInstrumentName).Unit.Should().Be("{delivery}");
        published.Single(p => p.Name == BusinessMetrics.PaymentInstantDurationInstrumentName).Unit.Should().Be("ms");
    }

    [Theory]
    [InlineData(BusinessMetrics.PaymentOutcome.Stored, "stored")]
    [InlineData(BusinessMetrics.PaymentOutcome.Duplicate, "duplicate")]
    [InlineData(BusinessMetrics.PaymentOutcome.ValidationFailed, "validation_failed")]
    public void RecordPaymentIntake_emits_exactly_one_measurement_with_the_outcome_and_scheme_tags(
        BusinessMetrics.PaymentOutcome outcome, string expectedTag)
    {
        using var metrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(metrics);

        metrics.RecordPaymentIntake(outcome, BusinessMetrics.PaymentScheme.Standard);

        var measurement = listener.Measurements.Should().ContainSingle().Which;
        measurement.InstrumentName.Should().Be(BusinessMetrics.PaymentIntakeInstrumentName);
        measurement.Value.Should().Be(1L);
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["outcome"] = expectedTag,
            ["payment.scheme"] = "standard"
        });
    }

    [Theory]
    [InlineData(BusinessMetrics.PaymentScheme.Standard, "standard")]
    [InlineData(BusinessMetrics.PaymentScheme.Instant, "instant")]
    public void RecordPaymentIntake_tags_the_payment_scheme(
        BusinessMetrics.PaymentScheme scheme, string expectedTag)
    {
        using var metrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(metrics);

        metrics.RecordPaymentIntake(BusinessMetrics.PaymentOutcome.Stored, scheme);

        listener.Measurements.Should().ContainSingle().Which.Tags["payment.scheme"].Should().Be(expectedTag);
    }

    [Theory]
    [InlineData(BusinessMetrics.InstantPaymentOutcome.Settled, "settled")]
    [InlineData(BusinessMetrics.InstantPaymentOutcome.Rejected, "rejected")]
    [InlineData(BusinessMetrics.InstantPaymentOutcome.Deferred, "deferred")]
    public void RecordInstantPaymentDuration_emits_exactly_one_measurement_with_only_the_outcome_tag(
        BusinessMetrics.InstantPaymentOutcome outcome, string expectedTag)
    {
        using var metrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(metrics);

        metrics.RecordInstantPaymentDuration(outcome, TimeSpan.FromMilliseconds(123));

        var measurement = listener.Measurements.Should().ContainSingle().Which;
        measurement.InstrumentName.Should().Be(BusinessMetrics.PaymentInstantDurationInstrumentName);
        measurement.Value.Should().Be(123d);
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?> { ["outcome"] = expectedTag });
    }

    [Fact]
    public void RecordInstantPaymentDuration_clamps_a_negative_duration_to_zero()
    {
        using var metrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(metrics);

        metrics.RecordInstantPaymentDuration(BusinessMetrics.InstantPaymentOutcome.Deferred, TimeSpan.FromMilliseconds(-50));

        listener.Measurements.Should().ContainSingle().Which.Value.Should().Be(0d);
    }

    [Theory]
    [InlineData(BusinessMetrics.TransactionIntakeOutcome.Accepted, "accepted")]
    [InlineData(BusinessMetrics.TransactionIntakeOutcome.Replayed, "replayed")]
    [InlineData(BusinessMetrics.TransactionIntakeOutcome.InFlight, "in_flight")]
    [InlineData(BusinessMetrics.TransactionIntakeOutcome.TransportFailed, "transport_failed")]
    public void RecordTransactionIntake_emits_exactly_one_measurement_with_only_the_outcome_tag(
        BusinessMetrics.TransactionIntakeOutcome outcome, string expectedTag)
    {
        using var metrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(metrics);

        metrics.RecordTransactionIntake(outcome);

        var measurement = listener.Measurements.Should().ContainSingle().Which;
        measurement.InstrumentName.Should().Be(BusinessMetrics.TransactionIntakeInstrumentName);
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?> { ["outcome"] = expectedTag });
    }

    [Theory]
    [InlineData(BusinessMetrics.TransactionProcessedOutcome.Completed, "completed")]
    [InlineData(BusinessMetrics.TransactionProcessedOutcome.BusinessRejected, "business_rejected")]
    public void RecordTransactionProcessed_emits_exactly_one_measurement_with_only_the_outcome_tag(
        BusinessMetrics.TransactionProcessedOutcome outcome, string expectedTag)
    {
        using var metrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(metrics);

        metrics.RecordTransactionProcessed(outcome);

        var measurement = listener.Measurements.Should().ContainSingle().Which;
        measurement.InstrumentName.Should().Be(BusinessMetrics.TransactionProcessedInstrumentName);
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?> { ["outcome"] = expectedTag });
    }

    [Fact]
    public void RecordStoreOperation_emits_exactly_one_measurement_with_only_the_closed_set_tags()
    {
        using var metrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(metrics);

        metrics.RecordStoreOperation(
            BusinessMetrics.StoreName.CoreBankInbox, BusinessMetrics.StoreKind.Inbox, BusinessMetrics.StoreOperationOutcome.Added);

        var measurement = listener.Measurements.Should().ContainSingle().Which;
        measurement.InstrumentName.Should().Be(BusinessMetrics.MessagingStoreOperationsInstrumentName);
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["messaging.store.name"] = "corebank-inbox",
            ["messaging.store.kind"] = "inbox",
            ["outcome"] = "added",
        });
    }

    [Theory]
    [InlineData(BusinessMetrics.StoreName.PaymentsOutbox, "payments-outbox")]
    [InlineData(BusinessMetrics.StoreName.CoreBankInbox, "corebank-inbox")]
    [InlineData(BusinessMetrics.StoreName.CoreBankOutbox, "corebank-outbox")]
    [InlineData(BusinessMetrics.StoreName.PaymentsInbox, "payments-inbox")]
    public void RecordStoreOperation_maps_every_store_name_to_its_closed_wire_value(
        BusinessMetrics.StoreName storeName, string expectedTag)
    {
        using var metrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(metrics);

        metrics.RecordStoreOperation(storeName, BusinessMetrics.StoreKind.Outbox, BusinessMetrics.StoreOperationOutcome.Duplicate);

        listener.Measurements.Should().ContainSingle().Which.Tags["messaging.store.name"].Should().Be(expectedTag);
    }

    [Fact]
    public void RecordItemProcessed_emits_exactly_one_measurement_with_only_the_closed_set_tags()
    {
        using var metrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(metrics);

        metrics.RecordItemProcessed(
            BusinessMetrics.StoreName.PaymentsOutbox, BusinessMetrics.StoreKind.Outbox, BusinessMetrics.ItemOutcome.TerminalFailed);

        var measurement = listener.Measurements.Should().ContainSingle().Which;
        measurement.InstrumentName.Should().Be(BusinessMetrics.MessagingItemsProcessedInstrumentName);
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["messaging.store.name"] = "payments-outbox",
            ["messaging.store.kind"] = "outbox",
            ["outcome"] = "terminal_failed",
        });
    }

    [Fact]
    public void RecordQueueDuration_records_the_elapsed_milliseconds_with_only_the_closed_set_tags()
    {
        using var metrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(metrics);

        metrics.RecordQueueDuration(
            BusinessMetrics.StoreName.PaymentsInbox, BusinessMetrics.StoreKind.Inbox, TimeSpan.FromMilliseconds(1234));

        var measurement = listener.Measurements.Should().ContainSingle().Which;
        measurement.InstrumentName.Should().Be(BusinessMetrics.MessagingQueueDurationInstrumentName);
        measurement.Value.Should().Be(1234d);
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["messaging.store.name"] = "payments-inbox",
            ["messaging.store.kind"] = "inbox",
        });
    }

    [Fact]
    public void RecordQueueDuration_clamps_a_negative_duration_to_zero_milliseconds()
    {
        using var metrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(metrics);

        metrics.RecordQueueDuration(
            BusinessMetrics.StoreName.CoreBankOutbox, BusinessMetrics.StoreKind.Outbox, TimeSpan.FromMilliseconds(-500));

        listener.Measurements.Should().ContainSingle().Which.Value.Should().Be(0d);
    }

    [Fact]
    public void RecordDelivery_emits_exactly_one_measurement_with_only_the_closed_set_tags()
    {
        using var metrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(metrics);

        metrics.RecordDelivery(
            BusinessMetrics.DeliveryDirection.Sent,
            BusinessMetrics.Transport.Http,
            BusinessMetrics.MessageType.TransactionCommand,
            BusinessMetrics.DeliveryOutcome.Succeeded);

        var measurement = listener.Measurements.Should().ContainSingle().Which;
        measurement.InstrumentName.Should().Be(BusinessMetrics.MessagingDeliveriesInstrumentName);
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["messaging.direction"] = "sent",
            ["messaging.transport"] = "http",
            ["messaging.message.type"] = "transaction-command",
            ["outcome"] = "succeeded",
        });
    }

    [Theory]
    [InlineData(BusinessMetrics.MessageType.TransactionCompleted, "transaction-completed")]
    [InlineData(BusinessMetrics.MessageType.TransactionFailed, "transaction-failed")]
    [InlineData(BusinessMetrics.MessageType.BalanceUpdated, "balance-updated")]
    [InlineData(BusinessMetrics.MessageType.Unknown, "unknown")]
    public void RecordDelivery_maps_every_message_type_to_its_closed_wire_value(
        BusinessMetrics.MessageType messageType, string expectedTag)
    {
        using var metrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(metrics);

        metrics.RecordDelivery(
            BusinessMetrics.DeliveryDirection.Received, BusinessMetrics.Transport.Dapr, messageType, BusinessMetrics.DeliveryOutcome.Unknown);

        listener.Measurements.Should().ContainSingle().Which.Tags["messaging.message.type"].Should().Be(expectedTag);
    }
}
