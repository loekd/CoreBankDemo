# .NET Aspire Integration

This demo uses .NET Aspire 9 to orchestrate all services, making it incredibly easy to run and observe the entire system.

## What is Aspire?

.NET Aspire is an opinionated, cloud-ready stack for building observable, production-ready distributed applications. It provides:

- 🎯 **Service orchestration** - Start everything with one command
- 📊 **Built-in observability** - OpenTelemetry everywhere
- 🔍 **Aspire Dashboard** - Real-time logs, traces, and metrics
- 🏥 **Health checks** - Automatic service health monitoring
- 🔗 **Service discovery** - Services find each other automatically

## Architecture

```
CoreBankDemo.AppHost (Aspire)
├── Jaeger Container (Port 16686, 4317, 4318)
├── Core Bank API (Port 5032)
│   └── Uses: ServiceDefaults
└── Payments API (Port 5294)
    └── Uses: ServiceDefaults
```

## Project Structure

### CoreBankDemo.AppHost
The orchestrator that starts everything:
- Defines all services and their dependencies
- Configures environment variables
- Sets up service-to-service communication
- Launches containers (Jaeger)

### CoreBankDemo.ServiceDefaults
Shared configuration for all services:
- OpenTelemetry tracing and metrics
- Health checks
- Service discovery
- Resilience patterns
- Logging configuration

## Starting the Demo

### Simple Start
```bash
cd CoreBankDemo.AppHost
dotnet run
```

Or use the helper script:
```bash
./start-aspire.sh
```

### What Happens
1. ✅ Aspire pulls and starts Jaeger container
2. ✅ Starts Core Bank API on port 5032
3. ✅ Starts Payments API on port 5294
4. ✅ Configures all services to send telemetry to Jaeger
5. ✅ Opens Aspire Dashboard on port 15888

## Aspire Dashboard Features

Access at: **http://localhost:15888**

### Resources Tab
- View all running services and containers
- See health status (healthy/unhealthy)
- Start/stop individual services
- View environment variables and configuration

### Console Logs Tab
- Live streaming logs from all services
- Filter by service
- Search and highlight
- Structured logging with colors

### Traces Tab
- Distributed traces from OpenTelemetry
- Click to see trace details
- View service dependencies
- Identify slow operations

### Metrics Tab
- Real-time metrics dashboards
- HTTP request rates
- Response times
- Resource usage (CPU, memory)

## Service Defaults

Both APIs use `builder.AddServiceDefaults()` which automatically adds:

### OpenTelemetry
```csharp
// Tracing
- ASP.NET Core instrumentation
- HTTP client instrumentation
- OTLP exporter to Jaeger

// Metrics
- ASP.NET Core metrics
- HTTP client metrics
- Runtime metrics
```

### Health Checks
```csharp
// Automatic endpoints
- /health - Overall health
- /alive - Liveness probe
```

### Resilience
- Standard resilience patterns included
- HTTP client with retry/circuit breaker

## Configuration

### AppHost.cs
```csharp
// Jaeger container
var jaeger = builder.AddContainer("jaeger", "jaegertracing/all-in-one")
    .WithHttpEndpoint(16686, name: "jaeger-ui")
    .WithEndpoint(4317, name: "otlp-grpc");

// Core Bank API
var coreBankApi = builder.AddProject<Projects.CoreBankDemo_CoreBankAPI>("corebank-api")
    .WithHttpEndpoint(port: 5032)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", "http://jaeger:4317");

// Payments API
var paymentsApi = builder.AddProject<Projects.CoreBankDemo_PaymentsAPI>("payments-api")
    .WithHttpEndpoint(port: 5294)
    .WithEnvironment("CoreBankApi__BaseUrl", coreBankApi.GetEndpoint("http"))
    .WithReference(coreBankApi); // Service-to-service communication
```

## Benefits for the Demo

### 1. Single Command Start
No more juggling multiple terminals:
```bash
# Before Aspire (3 terminals)
Terminal 1: docker compose up -d
Terminal 2: cd CoreBankDemo.CoreBankAPI && dotnet run
Terminal 3: cd CoreBankDemo.PaymentsAPI && dotnet run

# With Aspire (1 terminal)
cd CoreBankDemo.AppHost && dotnet run
```

### 2. Live Observability
- See logs from all services in one place
- Real-time trace visualization
- Metrics dashboards without extra setup

### 3. Easy Service Management
- Restart individual services from the dashboard
- See environment variables
- Monitor resource usage

### 4. Service Discovery
- Services automatically know how to reach each other
- No hardcoded URLs (Aspire injects them)
- Works the same in dev and production

### 5. Configuration Management
- Environment variables managed centrally
- Override per-service in AppHost
- No more appsettings.json juggling

## Demo Flow with Aspire

### Stage 0: Baseline
1. Start Aspire
2. Open Aspire Dashboard
3. Show all services running and healthy
4. Show live logs

### Stage 1: Retry & Circuit Breaker
1. Start DevProxy separately
2. Watch failures in Aspire Dashboard logs
3. See retries happening in real-time
4. View traces in both Aspire and Jaeger

### Stage 2: Outbox Pattern
1. Stop Core Bank API from Aspire Dashboard
2. Send payments → stored in outbox
3. Restart Core Bank API from dashboard
4. Watch OutboxProcessor logs in Aspire
5. See successful processing

### Stage 3: Inbox Pattern
1. Send duplicate requests
2. Filter logs by "idempotency" in Aspire
3. See cached responses in logs
4. View structured logging

### Stage 4: Ordering
1. Send rapid-fire payments
2. Watch parallel processing in logs
3. See partitioning in action
4. View metrics for throughput

## DevProxy Integration

DevProxy still needs to run separately for chaos testing:

```bash
# Terminal 2 (while Aspire runs in Terminal 1)
dotnet tool restore
dotnet devproxy --config-file devproxy.json
```

Then update `appsettings.Development.json`:
```json
{
  "CoreBankApi": {
    "BaseUrl": "http://localhost:8000"  // Point to DevProxy
  }
}
```

Restart Payments API from Aspire Dashboard to pick up the change.

## Troubleshooting

### Aspire Dashboard not opening?
Check: http://localhost:15888

If it doesn't open automatically, look for the URL in the console output.

### Services not starting?
1. Check Docker is running
2. Check ports 5032, 5294, 16686, 15888 are free
3. View logs in Aspire Dashboard console tab

### Can't connect to Jaeger?
Aspire manages Jaeger lifecycle. If issues:
1. Stop Aspire (Ctrl+C)
2. Clean containers: `docker container prune`
3. Restart Aspire

### Database files?
SQLite databases are created in the API project directories:
- `CoreBankDemo.PaymentsAPI/payments.db`
- `CoreBankDemo.CoreBankAPI/corebank.db`

Use `./reset-demo.sh` to clean them.

## Production Considerations

Aspire isn't just for development! It can:

1. **Generate deployment manifests**
   ```bash
   dotnet run --project CoreBankDemo.AppHost -- --publisher manifest
   ```

2. **Deploy to Azure**
   - Azure Container Apps
   - Azure Kubernetes Service
   - Uses same observability stack

3. **Works with existing CI/CD**
   - Standard .NET build process
   - Container images from projects
   - Infrastructure as Code

## Learn More

- [.NET Aspire Documentation](https://learn.microsoft.com/en-us/dotnet/aspire/)
- [Aspire Components](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/components-overview)
- [Service Defaults](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/service-defaults)
- [Aspire Dashboard](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard)

## Why Aspire for This Demo?

Perfect for demonstrating resilience patterns because:

1. ✅ **Fast setup** - Audience sees code, not configuration
2. ✅ **Visual feedback** - Live logs show patterns in action
3. ✅ **Professional** - This is how modern .NET apps are built
4. ✅ **Reproducible** - Works the same on every machine
5. ✅ **Educational** - Shows best practices for observability

Aspire lets us focus on teaching resilience patterns, not fighting infrastructure!
