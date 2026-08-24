using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using CoreBankDemo.ServiceDefaults;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Instrumentation.GrpcNetClient;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.Hosting;

// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    extension<TBuilder>(TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        public TBuilder AddServiceDefaults(string serviceName)
        {
            return builder.AddServiceDefaults(serviceName, Array.Empty<string>());
        }

        public TBuilder AddServiceDefaults(string serviceName, params string[] additionalActivitySources)
        {
            builder.ConfigureOpenTelemetry(serviceName, additionalActivitySources);

            builder.AddDefaultHealthChecks();

            builder.Services.AddServiceDiscovery();

            builder.Services.ConfigureHttpClientDefaults(http =>
            {
                // Turn on resilience by default
                http.AddStandardResilienceHandler();

                // Turn on service discovery by default
                http.AddServiceDiscovery();
            });

            // Register distributed lock service only when DaprClient is available.
            // Services that don't use Dapr (e.g. read-only support APIs) skip this.
            builder.Services.AddSingleton<IDistributedLockService>(sp =>
            {
                var daprClient = sp.GetService<Dapr.Client.DaprClient>();
                if (daprClient is null)
                    return new NoOpDistributedLockService();
                var logger = sp.GetRequiredService<ILogger<DaprDistributedLockService>>();
                // No TimeProvider is registered by AddServiceDefaults itself (story 3.2's
                // scope is the lock port, not DI-wide time wiring); fall back to the real
                // clock when nothing else in the host has registered one.
                var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
                return new DaprDistributedLockService(daprClient, timeProvider, logger);
            });

            // Register the CloudEvent publisher only when DaprClient is already
            // registered in DI at this point (services that wire Dapr do so before
            // calling AddServiceDefaults). Deliberately no NoOp fallback here (unlike
            // IDistributedLockService above): a no-op publisher would silently discard
            // every published event, which is worse than a service without Dapr simply
            // never resolving IEventPublisher at all — that path throws the standard DI
            // "no service for type" exception at the call site instead of hiding a real
            // bug behind a black hole.
            if (builder.Services.Any(sd => sd.ServiceType == typeof(Dapr.Client.DaprClient)))
            {
                builder.Services.AddSingleton<IEventPublisher>(sp =>
                {
                    var daprClient = sp.GetRequiredService<Dapr.Client.DaprClient>();
                    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MessagingOutboxProcessingOptions>>();
                    var logger = sp.GetRequiredService<ILogger<DaprEventPublisher>>();
                    return new DaprEventPublisher(daprClient, options, logger);
                });
            }

            // Uncomment the following to restrict the allowed schemes for service discovery.
            // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
            // {
            //     options.AllowedSchemes = ["https"];
            // });

            return builder;
        }

        public TBuilder AddInboxProcessingOptions()
        {
            builder.Services.AddOptions<InboxProcessingOptions>()
                .BindConfiguration(InboxProcessingOptions.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            return builder;
        }

        public TBuilder AddOutboxProcessingOptions()
        {
            builder.Services.AddOptions<OutboxProcessingOptions>()
                .BindConfiguration(OutboxProcessingOptions.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            return builder;
        }

        public TBuilder AddMessagingOutboxProcessingOptions()
        {
            builder.Services.AddOptions<MessagingOutboxProcessingOptions>()
                .BindConfiguration(MessagingOutboxProcessingOptions.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            return builder;
        }

        private void ConfigureOpenTelemetry(string serviceName, string[] additionalActivitySources)
        {
            var otlpEndpoint = builder.ResolveOtlpEndpoint();

            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
            });

            builder.Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(serviceName))
                .WithMetrics(metrics =>
                {
                    metrics.AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation();

                    if (otlpEndpoint is not null)
                    {
                        metrics.AddOtlpExporter(options =>
                        {
                            options.Endpoint = otlpEndpoint;
                            options.Protocol = OtlpExportProtocol.Grpc;
                        });
                    }
                    else
                    {
                        metrics.AddOtlpExporter();
                    }
                })
                .WithTracing(tracing =>
                {
                    tracing.AddSource(builder.Environment.ApplicationName)
                        .AddSource(serviceName)
                        .AddAspNetCoreInstrumentation(tr =>
                            // Exclude health check requests from tracing
                            tr.Filter = context =>
                                !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                                && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                        )
                        .AddGrpcClientInstrumentation()
                        .AddHttpClientInstrumentation();

                    foreach (var sourceName in additionalActivitySources.Where(name => !string.IsNullOrWhiteSpace(name)))
                    {
                        tracing.AddSource(sourceName);
                    }

                    if (otlpEndpoint is not null)
                    {
                        tracing.AddOtlpExporter(options =>
                        {
                            options.Endpoint = otlpEndpoint;
                            options.Protocol = OtlpExportProtocol.Grpc;
                        });
                    }
                    else
                    {
                        tracing.AddOtlpExporter();
                    }
                });

            var activitySource = new ActivitySource(serviceName);
            builder.Services.AddSingleton(activitySource);
        }

        internal Uri? ResolveOtlpEndpoint()
        {
            // Prefer explicit Jaeger endpoint over Aspire's OTEL_EXPORTER_OTLP_ENDPOINT default.
            var endpointValue = builder.Configuration["JAEGER_OTLP_ENDPOINT"];
            if (string.IsNullOrWhiteSpace(endpointValue))
            {
                return null;
            }

            // Gate the first parse attempt on an explicit "://": without it, a bare
            // "host:port" value (e.g. "jaeger:4317") would still satisfy
            // Uri.TryCreate(..., UriKind.Absolute) whenever the host is a
            // syntactically valid URI scheme name — Uri would then read "jaeger" as
            // the scheme and "4317" as an opaque scheme-specific part instead of as
            // host:port, skipping the http:// normalization below entirely.
            if (endpointValue.Contains("://", StringComparison.Ordinal)
                && Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpointUri))
            {
                return endpointUri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                    ? new UriBuilder(endpointUri)
                    {
                        Scheme = Uri.UriSchemeHttp,
                        Port = endpointUri.IsDefaultPort ? 4317 : endpointUri.Port
                    }.Uri
                    : endpointUri;
            }

            // A bare "host:port" value never has an explicit scheme to rewrite —
            // the "http://" prefix below is hardcoded, so the parsed scheme here
            // is always "http", never "tcp". No tcp-rewrite check needed.
            var normalizedEndpoint = $"http://{endpointValue}";
            if (Uri.TryCreate(normalizedEndpoint, UriKind.Absolute, out endpointUri))
            {
                return endpointUri;
            }

            throw new InvalidOperationException($"Invalid JAEGER_OTLP_ENDPOINT value '{endpointValue}'.");
        }

        private void AddDefaultHealthChecks()
        {
            builder.Services.AddHealthChecks()
                // Add a default liveness check to ensure app is responsive
                .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        }
    }

    /// <summary>
    /// Hosting-only: maps the dev-only /health and /alive endpoints against a
    /// live request pipeline. Requires a running <see cref="WebApplication"/>
    /// to exercise meaningfully (not just DI registration), which is out of
    /// scope for this project's DI-container-inspection test approach —
    /// excluded from the coverage gate rather than left uncovered by accident.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/dotnet/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }


        return app;
    }
}
