var builder = DistributedApplication.CreateBuilder(args);

// Add Jaeger for distributed tracing
var jaeger = builder.AddContainer("jaeger", "jaegertracing/all-in-one", "1.66.0")
    .WithHttpEndpoint(port: 16686, targetPort: 16686, name: "jaeger-ui")
    .WithEndpoint(port: 4317, targetPort: 4317, name: "otlp-grpc")
    .WithEndpoint(port: 4318, targetPort: 4318, name: "otlp-http")
    .WithEnvironment("COLLECTOR_OTLP_ENABLED", "true");

// Core Bank API (Legacy System)
// Ports are defined in launchSettings.json (5032)
var coreBankApi = builder.AddProject<Projects.CoreBankDemo_CoreBankAPI>("corebank-api")
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317")
    .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "grpc")
    .WithUrl("/swagger", "Swagger UI");

// Payments API (Main Service)
// Ports are defined in launchSettings.json (5294)
var paymentsApi = builder.AddProject<Projects.CoreBankDemo_PaymentsAPI>("payments-api")
    .WithEnvironment("CoreBankApi__BaseUrl", coreBankApi.GetEndpoint("http"))
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317")
    .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "grpc")
    .WithReference(coreBankApi)
    .WithUrl("/swagger", "Swagger UI"); 

builder.Build().Run();
