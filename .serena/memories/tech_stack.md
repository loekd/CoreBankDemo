# Tech Stack

- **Language:** C# / .NET 10
- **Framework:** ASP.NET Core (minimal APIs + controllers)
- **Orchestration:** .NET Aspire (AppHost)
- **ORM:** Entity Framework Core (PostgreSQL via Npgsql)
- **Messaging:** Dapr (pub/sub, distributed lock)
- **Observability:** OpenTelemetry, Jaeger, Aspire Dashboard
- **Chaos testing:** Microsoft Dev Proxy
- **Load testing:** k6 (orchestrated via Aspire container)
- **Shared library:** CoreBankDemo.Messaging (base inbox/outbox classes)
- **Resilience:** Microsoft.Extensions.Http.Resilience (AddStandardResilienceHandler)

## Projects in Solution
- `CoreBankDemo.PaymentsAPI` - Payment service
- `CoreBankDemo.CoreBankAPI` - Core banking service
- `CoreBankDemo.Messaging` - Shared inbox/outbox base classes
- `CoreBankDemo.ServiceDefaults` - Aspire shared config, distributed lock, cloud event types
- `CoreBankDemo.AppHost` - Aspire orchestration
- `CoreBankDemo.LoadTests` - Load test Aspire host (k6-based)
- `CoreBankDemo.LoadTestSupport` - Assert API for load tests
