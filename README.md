# Core Banking Demo - Building Resilient Mission-Critical Systems

A demonstration project showing resilience patterns for mission-critical banking systems, built with .NET 10 and orchestrated with .NET Aspire. Designed for a 55-minute conference talk.

## What's Special

- **One-Command Start** - `./start-aspire.sh` launches everything
- **.NET Aspire** - Modern orchestration with built-in observability
- **Real-World Patterns** - Retry, Circuit Breaker, Outbox, Inbox, Ordering
- **Shared Libraries** - Reusable inbox/outbox base classes eliminate duplication
- **Type-Safe Constants** - No magic strings, centralized configuration
- **Live Observability** - Aspire Dashboard + Jaeger tracing
- **Chaos Testing** - Dev Proxy for failure injection
- **Production-Ready** - Patterns used in actual banking systems

## Architecture

```
┌─────────────────┐         ┌──────────────┐         ┌─────────────────┐
│  Payments API   │────────▶│  Dev Proxy   │────────▶│ Core Bank API   │
│  (Your Service) │         │  (Chaos)     │         │  (Legacy SaaS)  │
└─────────────────┘         └──────────────┘         └─────────────────┘
        │                                                      │
        │ Outbox Pattern                                      │ Inbox Pattern
        ▼                                                      ▼
   ┌─────────┐                                           ┌─────────┐
   │ SQLite  │                                           │ SQLite  │
   └─────────┘                                           └─────────┘
        │                                                      │
        └──────────────────────────────────────────────────────┘
                          Both send traces to
                                  │
                                  ▼
                           ┌──────────┐
                           │  Jaeger  │
                           └──────────┘
```

## Quick Start

### Option 1: Using Aspire (Recommended)

```bash
# Start everything with .NET Aspire
cd CoreBankDemo.AppHost
aspire run
```

This will launch:
- Payments API (http://localhost:5294)
- Core Bank API (http://localhost:5032)
- Dev Proxy (http://localhost:8000) - Chaos engineering proxy
- PostgreSQL databases (paymentsdb, corebankdb)
- Jaeger (http://localhost:16686)
- Aspire Dashboard (http://localhost:15888)

**Everything runs automatically - no manual steps needed!**


### Access UIs

- **Aspire Dashboard:** http://localhost:15888 (when using Aspire)
- **Jaeger Tracing:** http://localhost:16686
- **Payments API OpenAPI:** http://localhost:5294/openapi/v1.json
- **Core Bank API OpenAPI:** http://localhost:5032/openapi/v1.json
- **Health Checks:** 
  - Payments API: http://localhost:5294/health
  - Core Bank API: http://localhost:5032/health

## Demo Flow

### Stage 0: Baseline (5 min)

**Goal:** Show basic architecture working perfectly.

**Configuration:**
- All features disabled
- Direct calls to Core Bank API

**Demo:**
1. Send payment request via `demo-requests.http`
2. Show successful processing
3. Explain architecture: Payments API → Core Bank API

**Key Point:** Works great when everything is perfect!

---

### Stage 1: Retry & Circuit Breaker (10 min)

**Goal:** Handle transient failures.

**Setup:**
1. Enable DevProxy: Set `"enabled": true` in `devproxy.json` for `GenericRandomErrorPlugin`
2. Restart Aspire (Ctrl+C and `dotnet run` again)
   - Aspire will automatically restart DevProxy with new configuration

**Demo:**
1. Show random failures (503, 429, 500)
2. Explain `AddStandardResilienceHandler()` in `Program.cs:17`
3. Show retries in logs
4. Open Jaeger and show:
   - Multiple HTTP spans for retries
   - Latency measurements
   - Success after retries

**What's included:**
- Exponential backoff retry
- Circuit breaker
- Timeout policies

**Code Reference:** `CoreBankDemo.PaymentsAPI/Program.cs:17`

**Key Point:** Handles ~95% of real-world transient issues.

---

### Stage 2: Outbox Pattern (15 min)

**Goal:** Handle longer outages without losing requests.

**Configuration:**
Already enabled in `appsettings.Development.json`:
```json
"Features": {
  "UseOutbox": true
}
```

**Demo:**
1. Keep DevProxy error rate high or stop Core Bank API in Aspire Dashboard
2. Send payment requests
3. Show 202 Accepted response with "Pending" status
4. Query outbox: `GET http://localhost:5294/api/outbox`
5. Show messages stored in PostgreSQL (paymentsdb)
6. Restart Core Bank API in Aspire Dashboard or reduce DevProxy errors
7. Watch OutboxProcessor logs in Aspire Dashboard - see automatic retry
8. Query outbox again - show "Completed" status

**How it works:**
- Payment requests stored in local database
- Background service (`OutboxProcessor.cs`) polls every 5 seconds
- Retries failed messages up to 5 times
- Eventually consistent processing

**Code References:**
- Outbox storage: `CoreBankDemo.PaymentsAPI/Program.cs:53-79`
- Background processor: `CoreBankDemo.PaymentsAPI/OutboxProcessor.cs`
- Database model: `CoreBankDemo.PaymentsAPI/OutboxMessage.cs`

**Key Point:** Don't lose customer requests! But this introduces new problems...

---

### Stage 3: Inbox Pattern (10 min)

**Goal:** Prevent duplicate processing (idempotency).

**Problem:**
- Retry can cause duplicate transactions
- Customer charged twice!

**Configuration:**
Enable Inbox in Core Bank API `appsettings.Development.json`:
```json
"Features": {
  "UseInbox": true
}
```

**Demo:**
1. Show idempotency key in transaction request
2. Manually send same transaction twice:
   ```http
   POST http://localhost:5032/api/transactions/process
   {
     "fromAccount": "NL91ABNA0417164300",
     "toAccount": "NL20INGB0001234567",
     "amount": 100.00,
     "currency": "EUR",
     "idempotencyKey": "test-123"
   }
   ```
3. Query inbox: `GET http://localhost:5032/api/inbox`
4. Show same `transactionId` returned for duplicate
5. Explain: first request creates transaction, second returns cached result

**How it works:**
- Core Bank API stores processed requests with idempotency key
- Duplicate requests return original response
- No duplicate charges

**Code Reference:** `CoreBankDemo.CoreBankAPI/Program.cs:36-90`

**Key Point:** Critical for financial systems - exactly-once processing.

---

### Stage 4: Message Ordering (10 min)

**Goal:** Maintain per-account ordering while scaling.

**Problem:**
- Multiple payments from same account processed out of order
- Balance calculations can be wrong
- Race conditions

**Configuration:**
Already enabled in `appsettings.Development.json`:
```json
"Features": {
  "UseOrdering": true
}
```

**Demo:**
1. Create multiple payments from same account quickly
2. Show `PartitionKey` in outbox (set to `FromAccount`)
3. Explain processing logic:
   - One message per partition at a time
   - Multiple partitions processed concurrently
   - Ordering preserved within each account
4. Show logs: messages from different accounts processed in parallel

**How it works:**
- Each message partitioned by `FromAccount`
- Processor takes oldest message per partition
- Sequential processing per account
- Parallel processing across accounts

**Code References:**
- Partition key: `CoreBankDemo.PaymentsAPI/Program.cs:73`
- Ordering logic: `CoreBankDemo.PaymentsAPI/OutboxProcessor.cs:44-79`

**Key Point:** Balance scalability with ordering guarantees.

---

### Stage 5: Wrap-up (5 min)

**Tools that help:**
- **.NET Aspire:** Orchestration and observability (see [ASPIRE.md](ASPIRE.md))
- **Dev Proxy:** Chaos testing in development
- **Jaeger:** Distributed tracing and observability
- **DevContainer:** Consistent development environment
- **Entity Framework:** Simple persistence
- **OpenTelemetry:** Standard instrumentation

**Pattern Layering:**
1. **Retry/Circuit Breaker:** First line of defense (transient failures)
2. **Outbox:** Second line (sustained outages)
3. **Inbox:** Data integrity (idempotency)
4. **Ordering:** Business logic guarantees (per-entity consistency)

**Key Takeaways:**
1. Resilience is layered - no single solution
2. Observability is not optional
3. Test failure scenarios in development
4. Tools exist - don't build everything from scratch

## Feature Flags

Control patterns via `appsettings.json`:

```json
"Features": {
  "UseOutbox": false,    // Store-and-forward for outages
  "UseInbox": false,     // Idempotency/deduplication
  "UseOrdering": false   // Per-account ordering
}
```

## Test Accounts

Valid accounts in Core Bank API:
- `NL91ABNA0417164300`
- `NL20INGB0001234567`
- `NL39RABO0300065264`

## DevProxy Configuration

### Enable Random Errors
Edit `devproxy.json`:
```json
{
  "name": "GenericRandomErrorPlugin",
  "enabled": true  // Set to true
}
```

### Add Latency
```json
{
  "name": "LatencyPlugin",
  "enabled": true  // Set to true
}
```

### Rate Limiting
```json
{
  "name": "RateLimitingPlugin",
  "enabled": true  // Set to true
}
```

## Database Files

- `paymentsdb` - Payments API outbox and inbox (PostgreSQL)
- `corebankdb` - Core Bank API inbox and messaging outbox (PostgreSQL)

To reset state, delete the database containers or clear the tables.

## Security Notes

The load test configuration uses a hardcoded Redis password (`myredispassword123`) in the following files:
- `CoreBankDemo.LoadTests/AppHost.cs`
- `dapr/components/lockstore-redis.yaml`
- `dapr/components/pubsub-redis.yaml`
- `dapr/components-loadtest/lockstore-redis.yaml`
- `dapr/components-loadtest/pubsub-redis.yaml`

This is intentional — the Redis instance is **disposable and local-only**, spun up and torn down by Aspire for each load test run. The password has no security implications outside that ephemeral container. Do not use these credentials for any real environment.

## Troubleshooting

**DevProxy not working?**
```bash
# Ensure the devproxy executable is in the project root
./devproxy --help
# Or check the devproxy.json configuration file
```

**Port already in use?**
```bash
lsof -ti:5032 | xargs kill  # Core Bank API
lsof -ti:5294 | xargs kill  # Payments API
lsof -ti:8000 | xargs kill  # Dev Proxy
```

**Jaeger not showing traces?**
- Check `OTEL_EXPORTER_OTLP_ENDPOINT` in `appsettings.json`
- Ensure docker compose is running: `docker compose ps`

**Database errors?**
```bash
# Clear PostgreSQL databases via Aspire Dashboard or restart with clean volumes
# Databases are automatically created on startup
```

## Load Testing

The project includes comprehensive load tests that validate the system under concurrent load:

```bash
# Run load tests with k6
dotnet run --project CoreBankDemo.LoadTests
```

**What it tests:**
- Exactly-once processing under concurrent load (10 VUs submitting 1000+ transactions)
- Idempotency: ~10% are deliberate retry attempts with duplicate idempotency keys
- End-to-end flow: Payments API outbox → Core Bank API inbox → transaction processing
- No failed messages, no pending messages, no duplicate processing

**Configuration:** Edit `CoreBankDemo.LoadTests/appsettings.json`:
```json
{
  "LoadTest": {
    "TransactionCount": "1000",  // Total unique transactions
    "VuCount": "10"               // Concurrent virtual users
  }
}
```

The load test uses disposable PostgreSQL and Redis instances, seeded with 10 test accounts (€10M each). See [CoreBankDemo.LoadTests/README.md](CoreBankDemo.LoadTests/README.md) for details.

## Architecture & Technical Details

For detailed architecture information, database schemas, and implementation details, see:
- **[ARCHITECTURE.md](ARCHITECTURE.md)** - Complete technical architecture documentation
  - Shared library design (CoreBankDemo.Messaging)
  - Pattern implementations (Inbox/Outbox/Partitioning)
  - Database schemas
  - Configuration options
  - Design decisions and rationale
  - Load testing strategy

## Further Reading

- [.NET Aspire Documentation](https://learn.microsoft.com/en-us/dotnet/aspire/)
- [Resilience Patterns](https://learn.microsoft.com/en-us/dotnet/core/resilience/)
- [Transactional Outbox Pattern](https://microservices.io/patterns/data/transactional-outbox.html)
- [Idempotent Consumer Pattern](https://microservices.io/patterns/communication-style/idempotent-consumer.html)
- [Dev Proxy](https://learn.microsoft.com/en-us/microsoft-cloud/dev/dev-proxy/)
- [OpenTelemetry .NET](https://opentelemetry.io/docs/languages/net/)
- [Dapr Distributed Application Runtime](https://dapr.io/)
