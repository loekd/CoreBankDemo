using System.Diagnostics;
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
                return new DaprDistributedLockService(daprClient, logger);
            });

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

        private Uri? ResolveOtlpEndpoint()
        {
            // Prefer explicit Jaeger endpoint over Aspire's OTEL_EXPORTER_OTLP_ENDPOINT default.
            var endpointValue = builder.Configuration["JAEGER_OTLP_ENDPOINT"];
            if (string.IsNullOrWhiteSpace(endpointValue))
            {
                return null;
            }

            if (Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpointUri))
            {
                return endpointUri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                    ? new UriBuilder(endpointUri)
                    {
                        Scheme = Uri.UriSchemeHttp,
                        Port = endpointUri.IsDefaultPort ? 4317 : endpointUri.Port
                    }.Uri
                    : endpointUri;
            }

            var normalizedEndpoint = $"http://{endpointValue}";
            if (Uri.TryCreate(normalizedEndpoint, UriKind.Absolute, out endpointUri))
            {
                return endpointUri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                    ? new UriBuilder(endpointUri)
                    {
                        Scheme = Uri.UriSchemeHttp,
                        Port = endpointUri.IsDefaultPort ? 4317 : endpointUri.Port
                    }.Uri
                    : endpointUri;
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
