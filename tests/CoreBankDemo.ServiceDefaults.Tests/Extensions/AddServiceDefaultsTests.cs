using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults;
using Dapr.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.Extensions;

/// <summary>
/// Story 3.4: DI-container-inspection tests for <c>AddServiceDefaults</c> —
/// no live OTLP collector, no real Dapr sidecar, no network calls anywhere in
/// this class. OTel is proven by checking the SDK's own <see cref="TracerProvider"/>/
/// <see cref="MeterProvider"/> singleton descriptors are present, without ever
/// resolving them (resolving would build the real export pipeline); resilience +
/// service discovery are proven by building the actual delegating-handler chain
/// via <see cref="IHttpMessageHandlerFactory"/> for an arbitrary client name —
/// building a handler chain performs no I/O, only the eventual HTTP send would.
/// </summary>
public class AddServiceDefaultsTests
{
    private static WebApplicationBuilder CreateBuilder() => WebApplication.CreateSlimBuilder();

    // ---- OpenTelemetry ----

    [Fact]
    public void Registers_TracerProvider_and_MeterProvider_service_descriptors()
    {
        var builder = CreateBuilder();

        builder.AddServiceDefaults("test-service");

        builder.Services.Should().Contain(sd => sd.ServiceType == typeof(TracerProvider));
        builder.Services.Should().Contain(sd => sd.ServiceType == typeof(MeterProvider));
    }

    [Fact]
    public void Registers_a_singleton_ActivitySource_named_after_the_service()
    {
        var builder = CreateBuilder();

        builder.AddServiceDefaults("test-service");
        using var provider = builder.Services.BuildServiceProvider();
        var activitySource = provider.GetRequiredService<System.Diagnostics.ActivitySource>();

        activitySource.Name.Should().Be("test-service");
    }

    // ---- BusinessMetrics (story 6.5) ----

    [Fact]
    public void Registers_a_singleton_BusinessMetrics_recorder()
    {
        var builder = CreateBuilder();

        builder.AddServiceDefaults("test-service");
        using var provider = builder.Services.BuildServiceProvider();

        var first = provider.GetRequiredService<BusinessMetrics>();
        var second = provider.GetRequiredService<BusinessMetrics>();

        first.Should().BeSameAs(second, "every composition root must record through the same Meter instance");
    }

    [Fact]
    public void BusinessMetrics_MeterName_is_added_to_the_metrics_provider()
    {
        var builder = CreateBuilder();
        var exporter = new CollectingMetricExporter();

        builder.AddServiceDefaults("test-service");
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddReader(
                new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: int.MaxValue)));
        using var provider = builder.Services.BuildServiceProvider();

        using var meterProvider = provider.GetRequiredService<MeterProvider>();
        var metrics = provider.GetRequiredService<BusinessMetrics>();

        metrics.RecordPaymentIntake(BusinessMetrics.PaymentOutcome.Stored);
        meterProvider.ForceFlush();

        exporter.InstrumentNames.Should().Contain(BusinessMetrics.PaymentIntakeInstrumentName);
    }

    private sealed class CollectingMetricExporter : BaseExporter<Metric>
    {
        public List<string> InstrumentNames { get; } = [];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                InstrumentNames.Add(metric.Name);
            }

            return ExportResult.Success;
        }
    }

    [Fact]
    public void JAEGER_OTLP_ENDPOINT_override_does_not_prevent_OTel_registration()
    {
        var builder = CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JAEGER_OTLP_ENDPOINT"] = "http://jaeger:4317",
        });

        var act = () => builder.AddServiceDefaults("test-service");

        act.Should().NotThrow();
        builder.Services.Should().Contain(sd => sd.ServiceType == typeof(TracerProvider));
    }

    // ---- Health checks ----

    [Fact]
    public void Registers_the_self_health_check_tagged_live()
    {
        var builder = CreateBuilder();

        builder.AddServiceDefaults("test-service");
        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        options.Registrations.Should().ContainSingle(r => r.Name == "self")
            .Which.Tags.Should().Contain("live");
    }

    // ---- Service discovery + resilience on typed HttpClients ----

    [Fact]
    public void Typed_HttpClients_get_the_standard_resilience_handler_and_service_discovery_via_ConfigureHttpClientDefaults()
    {
        var builder = CreateBuilder();

        builder.AddServiceDefaults("test-service");
        using var provider = builder.Services.BuildServiceProvider();
        var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        var handlerTypeNames = WalkDelegatingHandlerChain(handlerFactory.CreateHandler("some-arbitrary-client-name"));

        handlerTypeNames.Should().Contain(name => name.Contains("Resilience"),
            "AddStandardResilienceHandler must apply to every typed/named client via ConfigureHttpClientDefaults");
        handlerTypeNames.Should().Contain(name => name.Contains("ServiceDiscovery") || name.Contains("Resolving"),
            "AddServiceDiscovery must apply to every typed/named client via ConfigureHttpClientDefaults");
    }

    private static List<string> WalkDelegatingHandlerChain(HttpMessageHandler handler)
    {
        var names = new List<string>();
        var current = handler;
        while (current is DelegatingHandler delegating)
        {
            names.Add(delegating.GetType().FullName ?? delegating.GetType().Name);
            current = delegating.InnerHandler;
            if (current is null)
            {
                break;
            }
        }

        return names;
    }

    // ---- IDistributedLockService (story 6.2, ADR-011: wiring-only coverage here) ----

    [Fact]
    public void IDistributedLockService_resolves_to_RedisDistributedLockService_when_IConnectionMultiplexer_is_registered()
    {
        var builder = CreateBuilder();
        builder.Services.AddSingleton(new Mock<StackExchange.Redis.IConnectionMultiplexer>().Object);

        builder.AddServiceDefaults("test-service");
        using var provider = builder.Services.BuildServiceProvider();
        var lockService = provider.GetRequiredService<IDistributedLockService>();

        lockService.Should().BeOfType<RedisDistributedLockService>();
    }

    [Fact]
    public void IDistributedLockService_resolves_to_NoOpDistributedLockService_when_IConnectionMultiplexer_is_absent()
    {
        var builder = CreateBuilder();

        builder.AddServiceDefaults("test-service");
        using var provider = builder.Services.BuildServiceProvider();
        var lockService = provider.GetRequiredService<IDistributedLockService>();

        lockService.Should().BeOfType<NoOpDistributedLockService>();
    }

    [Fact]
    public void Processor_start_gate_is_always_open_by_default()
    {
        var builder = CreateBuilder();

        builder.AddServiceDefaults("test-service");
        using var provider = builder.Services.BuildServiceProvider();

        provider.GetRequiredService<IProcessorStartGate>().Should().BeOfType<ProcessorStartGate>();
        provider.GetRequiredService<IProcessorStartGatePublisher>()
            .Should().BeSameAs(provider.GetRequiredService<IProcessorStartGate>());
    }

    [Fact]
    public void Processor_start_gate_uses_the_shared_Redis_connection_when_enabled()
    {
        var builder = CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProcessorStartGate:Enabled"] = "true",
            ["ProcessorStartGate:ExpectedParticipants"] = "4"
        });
        var multiplexer = new Mock<StackExchange.Redis.IConnectionMultiplexer>();
        multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(Mock.Of<StackExchange.Redis.IDatabase>());
        multiplexer.Setup(m => m.GetSubscriber(It.IsAny<object>()))
            .Returns(Mock.Of<StackExchange.Redis.ISubscriber>());
        builder.Services.AddSingleton(multiplexer.Object);

        builder.AddServiceDefaults("test-service");
        using var provider = builder.Services.BuildServiceProvider();

        provider.GetRequiredService<IProcessorStartGate>().Should().BeOfType<RedisProcessorStartGate>();
        provider.GetRequiredService<IProcessorStartGatePublisher>()
            .Should().BeSameAs(provider.GetRequiredService<IProcessorStartGate>());
    }

    // ---- IEventPublisher (story 3.3, newly wired by this story) ----

    [Fact]
    public void IEventPublisher_resolves_to_DaprEventPublisher_when_DaprClient_is_registered_before_AddServiceDefaults()
    {
        var builder = CreateBuilder();
        builder.Services.AddSingleton(new Mock<DaprClient>().Object);

        builder.AddServiceDefaults("test-service");
        using var provider = builder.Services.BuildServiceProvider();
        var publisher = provider.GetRequiredService<IEventPublisher>();

        publisher.Should().BeOfType<DaprEventPublisher>();
    }

    [Fact]
    public void IEventPublisher_is_not_registered_at_all_when_DaprClient_is_absent()
    {
        var builder = CreateBuilder();

        builder.AddServiceDefaults("test-service");
        using var provider = builder.Services.BuildServiceProvider();

        builder.Services.Should().NotContain(sd => sd.ServiceType == typeof(IEventPublisher));
        var act = () => provider.GetRequiredService<IEventPublisher>();
        act.Should().Throw<InvalidOperationException>(
            "an unregistered IEventPublisher must surface the standard DI 'no service for type' failure, never a silent no-op");
    }

    [Fact]
    public void IEventPublisher_is_not_registered_when_DaprClient_is_only_added_after_AddServiceDefaults_runs()
    {
        // Documents the deliberate, eager (registration-time) check: unlike
        // IDistributedLockService's lazy per-resolution factory check,
        // IEventPublisher's presence is decided once, at AddServiceDefaults
        // call time. Registering DaprClient afterward does not retroactively
        // add it — services that use Dapr are expected to register DaprClient
        // before calling AddServiceDefaults.
        var builder = CreateBuilder();

        builder.AddServiceDefaults("test-service");
        builder.Services.AddSingleton(new Mock<DaprClient>().Object);
        using var provider = builder.Services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IEventPublisher>();
        act.Should().Throw<InvalidOperationException>();
    }
}
