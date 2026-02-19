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
using OpenTelemetry.Metrics;
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

            // Register distributed lock service
            builder.Services.AddSingleton<IDistributedLockService, DaprDistributedLockService>();

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
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
            });

            builder.Services.AddOpenTelemetry()
                .WithMetrics(metrics =>
                {
                    metrics.AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation();
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
                        // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                        //.AddGrpcClientInstrumentation()
                        .AddHttpClientInstrumentation();

                    foreach (var sourceName in additionalActivitySources.Where(name => !string.IsNullOrWhiteSpace(name)))
                    {
                        tracing.AddSource(sourceName);
                    }
                });

            builder.AddOpenTelemetryExporters(serviceName);
        }

        private void AddOpenTelemetryExporters(string serviceName)
        {
            // Use a custom env var that Aspire won't override (Aspire auto-injects OTEL_EXPORTER_OTLP_ENDPOINT to its dashboard)
            var jaegerEndpoint = builder.Configuration["JAEGER_OTLP_ENDPOINT"];

            if (!string.IsNullOrWhiteSpace(jaegerEndpoint))
            {
                builder.Services.AddOpenTelemetry().UseOtlpExporter(OtlpExportProtocol.Grpc, new Uri(jaegerEndpoint));
            }
            else
            {
                builder.Services.AddOpenTelemetry().UseOtlpExporter();
            }

            var activitySource = new ActivitySource(serviceName);
            builder.Services.AddSingleton(activitySource);
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

    public static WebApplication RecreateSqliteDatabase(this WebApplication app, string databaseFileName)
    {
        var basePath = app.Environment.ContentRootPath;
        var databasePath = Path.IsPathRooted(databaseFileName)
            ? databaseFileName
            : Path.Combine(basePath, databaseFileName);

        var walPath = databasePath + "-wal";
        var shmPath = databasePath + "-shm";

        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }

        if (File.Exists(walPath))
        {
            File.Delete(walPath);
        }

        if (File.Exists(shmPath))
        {
            File.Delete(shmPath);
        }

        return app;
    }
}
