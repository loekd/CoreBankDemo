# Core Banking Demo - Building Resilient Mission-Critical Systems

A demonstration project showing resilience patterns for mission-critical banking systems, built with .NET 10 and orchestrated with .NET Aspire. Designed for a 55-minute conference talk.

## What's Special

- **One-Command Start** - `dotnet run --project CoreBankDemo.AppHost` launches everything
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
   ┌────────────┐                                     ┌────────────┐
   │ PostgreSQL │                                     │ PostgreSQL │
   └────────────┘                                     └────────────┘
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
- Payments API (http://127.0.0.1:5294)
- Core Bank API (http://127.0.0.1:5032)
- Dev Proxy (http://localhost:8000) - Chaos engineering proxy
- PostgreSQL databases (paymentsdb, corebankdb)
- Jaeger (http://localhost:16686)
- Aspire Dashboard (http://localhost:15888)

**Everything runs automatically - no manual steps needed!**


### Access UIs

- **Aspire Dashboard:** http://localhost:15888 (when using Aspire)
- **Jaeger Tracing:** http://localhost:16686
- **Payments API OpenAPI:** http://127.0.0.1:5294/openapi/v1.json
- **Core Bank API OpenAPI:** http://127.0.0.1:5032/openapi/v1.json
- **Health Checks:** 
  - Payments API: http://127.0.0.1:5294/health
  - Core Bank API: http://127.0.0.1:5032/health

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
1. Enable DevProxy: set `"enabled": true` in `CoreBankDemo.AppHost/devproxy/config/devproxyrc.json` for `GenericRandomErrorPlugin`
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
4. Query outbox: `GET http://127.0.0.1:5294/api/outbox`
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
   POST http://127.0.0.1:5032/api/transactions/process
   {
     "fromAccount": "NL91ABNA0417164300",
     "toAccount": "NL20INGB0001234567",
     "amount": 100.00,
     "currency": "EUR",
     "idempotencyKey": "test-123"
   }
   ```
3. Query inbox: `GET http://127.0.0.1:5032/api/inbox`
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
- **.NET Aspire:** Orchestration and observability (see [Aspire docs](https://learn.microsoft.com/en-us/dotnet/aspire/))
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
Edit `CoreBankDemo.AppHost/devproxy/config/devproxyrc.json`:
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
- `dapr/components/pubsub-redis.yaml`
- `dapr/components-loadtest/pubsub-redis.yaml`

This is intentional — the Redis instance is **disposable and local-only**, spun up and torn down by Aspire for each load test run. The password has no security implications outside that ephemeral container. Do not use these credentials for any real environment.

## Troubleshooting

**DevProxy not working?**
```bash
# Ensure the devproxy executable is in the project root
./devproxy --help
# Or check the devproxyrc.json configuration file
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

## Automated Tests

Tests are split into three tiers (see [ADR-016](docs/adr/ADR-016-postgresql-testcontainers-persistence-testing.md)):

```bash
# Tier 1 — fast unit tests. No Docker required.
dotnet test CoreBankDemo.UnitTests.slnf

# Tier 2 — persistence integration tests against a real, disposable PostgreSQL
# container (postgres:18.3). Requires a running container runtime.
dotnet test CoreBankDemo.IntegrationTests.slnf

# Full gate — runs both tiers and enforces the >=90% line-coverage threshold.
dotnet test CoreBankDemo.Rebuild.slnf
```

Tier 3 is the k6/Aspire acceptance harness described under **Load Testing** below.

The persistence tier starts **one** PostgreSQL container per test assembly on a
Testcontainers-generated host port (never a fixed port, so it cannot collide with a running
AppHost) and gives every test its own freshly created database. If no container runtime is
available the integration target fails with remediation instructions — it is never skipped or
reported green. There is no SQLite or EF Core InMemory fallback anywhere in this repository.

## Load Testing

The project includes comprehensive load tests that validate the system under concurrent load:

```bash
# Start the disposable load-test topology and wait for the accepted k6 resource
aspire start --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --non-interactive
aspire wait loadtest-support --non-interactive
aspire wait k6 --non-interactive
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

**MCP Integration:** The LoadTestSupport service exposes an MCP server at `http://localhost:5181/` for agent-based orchestration. See `mcp-config.example.json` and [CoreBankDemo.LoadTestSupport/README.md](CoreBankDemo.LoadTestSupport/README.md) for connection instructions.

## Presentation Console (DemoRunner)

`CoreBankDemo.DemoRunner` is a standalone, mouse-and-keyboard terminal operator console for live demonstrations (Story 7.4, ADR-015). It is a local tool only: it references no banking implementation project, connects to no database/Redis/Dapr/container socket directly, and is never a prerequisite for development, tests, or the banking services themselves. It uses only known HTTP endpoints and supported Aspire CLI operations. `demo-requests.http` and `payment-idempotency-tests.http` remain the supported fallback whenever the console is unavailable.

### One-command start

```bash
# Open the reusable operator console
dotnet run --project CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj

# Print prerequisites and current port state; starts nothing
dotnet run --project CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj -- --doctor
```

The console has five capability-driven workspaces:

1. **Operations** — submit standard or instant payments, choose Generated/Supplied/Omitted idempotency, resend a stable key, query outcomes, and run cancellable bounded bursts.
2. **Resources** — start or attach Regular/LoadTests, stop or switch only runner-owned AppHosts, and run confirmed allow-listed resource Start/Stop/Restart commands against a freshly fingerprinted graph.
3. **Evidence/Results** — inspect bounded redacted request/response evidence with topology and generation provenance, optionally wrap the raw view, and explicitly export the current session.
4. **Load Test** — run the accepted Reset → Run → Wait → Assert → Investigate workflow and read the five invariants plus inline-instant-settlement evidence.
5. **Faults** — stage error-rate, latency-band, and throttling levels from named presets or by hand, apply them in one write, and drop every knob to zero with `0`. Levels are injected only through Dev Proxy, by writing a gitignored generated session config the proxy reloads on its own (ADR-019); no checked-in Dev Proxy profile is ever written. Arming is a launch-time property set in **Resources** before an AppHost start, and is **off by default** — Dev Proxy is opt-in.

### Shortcuts

| Key | Action |
|---|---|
| `1` | Operations |
| `2` | Resources |
| `3` | Evidence/Results |
| `4` | Load Test |
| `5` | Faults |
| `0` | Panic-off — every fault knob to zero, applied immediately, from any workspace, no confirmation |
| `R` | Refresh live Aspire state |
| `Q` | Quit (stops only child processes this session started; never touches an attached/unowned topology) |

All mouse actions have keyboard equivalents. Destructive actions open a modal with **Cancel focused**; uppercase `Y` confirms and Escape cancels. At 80×24 the navigation rail compacts but remains visible.

### Recovery and evidence

- Resource and topology transitions resolve only from fresh Aspire snapshots. `Unknown` and `Unreachable` are distinct and disable mutation.
- Generated and supplied idempotency keys are stable for **Resend same key**. Omitted-key ambiguity is labeled `Ambiguous — not yet reconciled` and is never retried automatically.
- Evidence is session-local and never restored on relaunch. Explicit exports are written under the gitignored `.demo-runner-exports/` directory.
- A topology switch retains earlier evidence with its original profile and run-generation label.
- Every evidence record is stamped with the fault levels in force when it was captured, so a `202 Pending` observed under injected latency is never confused with one observed under none.
- The console reports `Applied — not yet observed in traffic` until its own traffic actually carries an applied fault level; only then does the topology bar read `Faults in force`. The generated session config is deleted when the session stops owning the topology, so a later `aspire run` uses the checked-in profile again.

### Manual fallback

If the console is unavailable, use `demo-requests.http` / `payment-idempotency-tests.http` directly against the regular AppHost and the load-test/Aspire workflow for the acceptance proof. Banking behavior is unchanged.

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
