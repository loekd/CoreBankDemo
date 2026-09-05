# CoreBankDemo

A runnable reference implementation of **exactly-once payment processing** across two services that
fail independently. It is a working distributed system — .NET 10, .NET Aspire, PostgreSQL, Dapr,
Redis and OpenTelemetry — built to show what it actually takes to not lose, duplicate, or reorder a
payment when the network, the downstream, or a process goes away mid-flight.

Everything runs locally with one command. Nothing here talks to a real bank.

## What it demonstrates

| Concern | How it is solved here |
|---|---|
| Don't lose a request during an outage | **Transactional Outbox** — the payment is committed to `paymentsdb` in the same transaction that records the intent to forward it (ADR-002) |
| Don't charge twice | **Idempotent Inbox** — every command is stored and de-duplicated on its idempotency key *before* any business logic runs (ADR-001) |
| Don't reorder a customer's payments | **Partitioned processing** — messages are hashed onto 4 fixed partitions; one worker holds one partition at a time, so ordering holds per key while partitions run in parallel (ADR-004, ADR-010) |
| Scale out without two workers racing | **Renewable Redis leases** — `DistributedLock.Redis` leases that renew while healthy and signal ownership loss (ADR-011) |
| Survive transient faults | **Retry, circuit breaker, timeout** via `AddStandardResilienceHandler()` on the HTTP client (ADR-006, ADR-007) |
| Answer "did it settle?" synchronously | **Instant rail** — `scheme: "instant"` attempts a budgeted inline forward and answers `200` with a committed outcome, or falls back to the same `202` store-and-forward path (ADR-018) |
| See the whole journey | **W3C trace context** persisted on every message and restored by the consumer, so one payment is one trace across HTTP *and* pub/sub hops (ADR-003, ADR-017) |
| Prove it, don't claim it | **k6 acceptance harness** asserting exactly-once, drain, balance conservation and stage cardinality under concurrent load (ADR-005) |

## Architecture

```
  ┌──────────────────────────────────────────────────────────────────┐
  │  DemoRunner — standalone terminal operator console               │
  │  Operations · Resources · Evidence/Results · Load Test · Faults  │
  │                                                                  │
  │  Speaks only public HTTP and the Aspire CLI — never a database,  │
  │  Redis, or Dapr socket directly:                                 │
  │    POST /api/payments       →  Payments API                      │
  │    aspire start/stop/list   →  the topology below                │
  │    /reset · /assert/*       →  LoadTestSupport (LoadTests only)  │
  └───────────────────┬──────────────────────────────────────────────┘
                      │
                      ▼

   ┌──────────────────┐   HTTP (Kiota)      ┌────────────┐   ┌──────────────────┐
   │   Payments API   │────────────────────▶│ Dev Proxy  │──▶│  Core Bank API   │
   │   (2 replicas)   │ fault injection     │ (optional) │   │   (2 replicas)   │
   │                  │                     └────────────┘   │                  │
   │ Outbox → forward │                                      │ Inbox  → execute │
   │ Inbox  ← events  │◀─────────────────────────────────────│ Outbox → publish │
   └────────┬─────────┘   Dapr pub/sub over Redis            └────────┬─────────┘
            │   topic: transaction-events                             │
            ▼                                                         ▼
     ┌──────────────┐             PostgreSQL 18.3              ┌──────────────┐
     │  paymentsdb  │                                          │  corebankdb  │
     └──────────────┘                                          └──────────────┘

   Redis 7.4  — Dapr pub/sub broker and partition-lease store
   Jaeger     — OTLP traces from every service and every Dapr sidecar
   Dashboard  — http://localhost:15888
```

The Payments API forwards to the Core Bank API over a **single HTTP integration** generated at build
time by Kiota from the checked-in OpenAPI contract (ADR-008, ADR-013). Dapr is used only for the
event hop back — the Core Bank API publishes `transaction.completed` / `transaction.failed` /
`balance.updated` CloudEvents, which the Payments API consumes into its own inbox so a deferred
payment eventually learns its committed outcome.

Both APIs run **two replicas** behind a stable Aspire endpoint (ADR-014), which is what makes the
distributed locking and partition ownership more than theoretical.

## Repository layout

| Project | Role |
|---|---|
| `CoreBankDemo.PaymentsAPI` | Accepts payments; Outbox forwarding, Inbox for CoreBank events, instant rail |
| `CoreBankDemo.CoreBankAPI` | Executes transactions and owns balances; idempotent Inbox, messaging Outbox |
| `CoreBankDemo.Messaging` | Transport-agnostic Inbox/Outbox kernel — base classes, `MessageConstants`, `PartitionHelper` |
| `CoreBankDemo.ServiceDefaults` | OpenTelemetry, health checks, distributed locking, CloudEvent types, business metrics |
| `CoreBankDemo.AppHost` | Aspire orchestration for regular development |
| `CoreBankDemo.LoadTests` | Second AppHost: disposable topology, k6, acceptance assertions |
| `CoreBankDemo.LoadTestSupport` | Test-only REST + MCP surface for reset, drain and invariant assertions |
| `CoreBankDemo.LoadTestInitializer` | One-shot reset/validate/gate-release before k6 starts |
| `CoreBankDemo.DemoRunner` | Standalone terminal operator console for driving demos |
| `tests/` | Unit and PostgreSQL persistence test projects |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A container runtime (Docker or Podman) — PostgreSQL, Redis and Jaeger run as containers
- [.NET Aspire CLI](https://learn.microsoft.com/dotnet/aspire/cli/overview) (`aspire`)
- [Dapr CLI](https://docs.dapr.io/getting-started/install-dapr-cli/), initialized (`dapr init`)
- [Dev Proxy 3.2.0](https://learn.microsoft.com/microsoft-cloud/dev/dev-proxy/) — required by the
  regular AppHost, which enables it by default (see [Fault injection](#fault-injection))

A devcontainer with all of the above is provided in `.devcontainer/`.

## Running it

```bash
dotnet tool restore          # required: Kiota generates the CoreBank client at build time
aspire run                   # uses aspire.config.json → CoreBankDemo.AppHost
```

That starts PostgreSQL, Redis, Jaeger, both APIs with their Dapr sidecars, and Dev Proxy.

| UI | URL |
|---|---|
| Aspire Dashboard | http://localhost:15888 |
| Jaeger | http://localhost:16686 |
| Payments API | http://127.0.0.1:5294 |
| Core Bank API | http://127.0.0.1:5032 |
| pgAdmin / RedisInsight | linked from the Aspire Dashboard |

**Running the AppHost inside a container?** The Aspire Dashboard and the Jaeger UI bind every
interface (`0.0.0.0`) rather than loopback, so a host-side port publish can actually reach them. A
devcontainer forwards them from the inside, where loopback would have been fine either way; a
sandbox needs them published explicitly, e.g. `sbx ports <sandbox> --publish 15888:15888/tcp`. The
APIs and the OTLP ingest ports stay on loopback on purpose — only in-container callers dial those.

Health endpoints live at `/health` on both APIs. Neither API serves a Swagger UI — the Core Bank
API's contract is the checked-in
[`CoreBankDemo.CoreBankAPI/OpenApi/corebank-api.json`](CoreBankDemo.CoreBankAPI/OpenApi/corebank-api.json),
which is also the source Kiota generates the Payments API's client from.

### Sending a payment

`demo-requests.http` and `payment-idempotency-tests.http` are ready-to-run request collections. The
core call:

```http
POST http://127.0.0.1:5294/api/payments
Content-Type: application/json
Idempotency-Key: demo-001

{
  "fromAccount": "NL91ABNA0417164300",
  "toAccount": "NL20INGB0001234567",
  "amount": 100.00,
  "currency": "EUR",
  "scheme": "standard"
}
```

- `scheme: "standard"` (or omitted) → **`202 Accepted`**, `Status: Pending`. The row is durable; the
  background processor forwards it and retries up to 5 times before going terminally `Failed`.
- `scheme: "instant"` → a budgeted inline attempt (9 s budget, 2.5 s per attempt, max 2 attempts).
  A committed outcome answers **`200 OK`**; anything not settled in budget falls back to `202` and
  the standard rail finishes it.
- The `Idempotency-Key` header is optional. Supplied or generated, resending the same key **never**
  creates a second row or a second delivery attempt — it replays the stored snapshot.

Three demo accounts are seeded on startup:

| Account | Holder | Balance |
|---|---|---|
| `NL91ABNA0417164300` | John Doe | € 5,000 |
| `NL20INGB0001234567` | Jane Smith | € 10,000 |
| `NL39RABO0300065264` | Bob Johnson | € 2,500 |

Core Bank API surface: `POST /api/transactions/process`, `GET /api/transactions/{idempotencyKey}`,
`POST /api/accounts/validate`, `GET /api/accounts/{accountNumber}`.

## Configuration

Message processing is configured per store, and the partition count is a validated system invariant
(ADR-010) — the services refuse to start on anything but 4.

```jsonc
// CoreBankDemo.PaymentsAPI/appsettings.json
"OutboxProcessing":  { "PartitionCount": 4, "LockExpirySeconds": 30, "PollingIntervalMs": 200 },
"InboxProcessing":   { "PartitionCount": 4, "LockExpirySeconds": 30, "PollingIntervalMs": 200 },
"Payments": {
  "InstantRail": {
    "Enabled": true,
    "BudgetMilliseconds": 9000,
    "AttemptTimeoutMilliseconds": 2500,
    "MaxAttempts": 2
  }
}
```

```jsonc
// CoreBankDemo.CoreBankAPI/appsettings.json
"MessagingOutboxProcessing": { "PubSubName": "pubsub", "TopicName": "transaction-events", ... }
```

There are no `UseOutbox` / `UseInbox` / `UseOrdering` feature switches — the patterns are the
system, not a demo toggle. The only host-level flag is `Features:UseDevProxy`.

## Fault injection

Dev Proxy sits between the Payments API and the Core Bank API and is enabled by default in
`CoreBankDemo.AppHost/appsettings.json`. It watches `http://127.0.0.1:5032/api/*` and, out of the
box, injects a 5% error rate (`devproxy-errors.json`), 20–200 ms of latency, and a 1000-request/min
rate limit — tune those in `CoreBankDemo.AppHost/devproxy/config/devproxyrc.json`.

To run without it (and without needing the binary on `PATH`):

```bash
aspire run -- --Features:UseDevProxy=false
```

Dev Proxy 3.2.0 cannot reload a changed config file, so config edits take a proxy restart
(ADR-019). DemoRunner starts the AppHost with `Features__UseDevProxy=false` unless you arm faults
in its **Resources** workspace, so the console's topology is Dev-Proxy-free by default.

## Operator console (DemoRunner)

`CoreBankDemo.DemoRunner` is a standalone mouse-and-keyboard terminal console for driving live
demonstrations (ADR-015). It is a **local tool only**: it references no banking project, touches no
database, Redis, Dapr or container socket directly, and is never a prerequisite for development,
tests, or the services themselves — it speaks only known HTTP endpoints and supported Aspire CLI
operations.

```bash
dotnet run --project CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj

# Print prerequisites and current port state; starts nothing
dotnet run --project CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj -- --doctor
```

Five workspaces, reachable with `1`–`5`:

1. **Operations** — submit standard or instant payments, choose Generated/Supplied/Omitted
   idempotency, resend a stable key, query outcomes, run cancellable bounded bursts.
2. **Resources** — start or attach the Regular/LoadTests topology, stop or switch only
   runner-owned AppHosts, run confirmed allow-listed Start/Stop/Restart against a freshly
   fingerprinted resource graph.
3. **Evidence/Results** — inspect bounded, redacted request/response evidence stamped with topology
   and run-generation provenance; export the session explicitly to the gitignored
   `.demo-runner-exports/`.
4. **Load Test** — run the Reset → Run → Wait → Assert → Investigate workflow and read the
   invariants plus inline-instant-settlement evidence.
5. **Faults** — stage error-rate, latency-band and throttling levels from named presets or by hand
   and apply them in one write. Levels are injected only through Dev Proxy, by writing a gitignored
   session config and then restarting the `devproxy` resource so it loads it — 3.2.0 cannot reload a
   changed config (ADR-019), so applying costs a brief proxy restart, which the workspace says up
   front. The checked-in Dev Proxy profile is never written to. Arming is a launch-time property set
   in **Resources** before a start, and is **off by default**.

`0` is panic-off — every fault knob to zero, applied immediately, from any workspace, without
confirmation. `R` refreshes live Aspire state; `Q` quits, stopping only child processes this session
started and never touching an attached topology. Every mouse action has a keyboard equivalent,
destructive actions open a modal with **Cancel** focused, and the layout stays usable at 80×24.

Evidence is session-local and is never restored on relaunch. Each record is stamped with the fault
levels in force when it was captured, so a `202 Pending` observed under injected latency is never
confused with one observed under none. The console reports `Applied — not yet observed in traffic`
until its own traffic actually carries an applied level; only then does the topology bar read
`Faults in force`, and a restart that fails leaves the levels reported as *not* applied.

If the console is unavailable, the `.http` files remain the supported fallback — banking behavior is
identical either way.

## Tests

Three tiers, cheapest first (ADR-012, ADR-016):

```bash
dotnet tool restore                                # once — Kiota client generation

dotnet test CoreBankDemo.UnitTests.slnf            # tier 1: fast, no Docker
dotnet test CoreBankDemo.IntegrationTests.slnf     # tier 2: real postgres:18.3 Testcontainer
dotnet test CoreBankDemo.Rebuild.slnf              # both tiers + the ≥90% line-coverage gate
```

Tier 2 starts one PostgreSQL container per test assembly on a Testcontainers-assigned port (never a
fixed one, so it cannot collide with a running AppHost) and gives every test a freshly created
database. With no container runtime available it **fails with remediation instructions** — it is
never skipped or reported green. There is no SQLite or EF Core InMemory substitute anywhere in this
repository.

`.github/workflows/ci.yml` runs `CoreBankDemo.Rebuild.slnf` on every push and PR to `main`.

## Load testing (tier 3)

The acceptance harness is a second, fully disposable Aspire topology that runs k6 against the real
services and then asserts the system's invariants.

```bash
aspire start --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --non-interactive
aspire wait loadtest-support --non-interactive
aspire wait k6 --non-interactive
```

Ten load-test accounts (`NL01LOAD0000000001` → `NL10LOAD0000000010`, € 10 M each) are seeded, k6's
virtual users race to submit payments, and ~10% are deliberate resends of an existing idempotency
key. After drain, `GET /assert/results?expectedUnique=<n>` checks:

- no failed and no pending/processing inbox messages,
- no idempotency key processed more than once,
- completed unique keys == the configured transaction count,
- inbox completed count == outbox submitted count,
- stage cardinality N/N/3N/3N across the four message stores,
- exactly the ten seeded accounts exist, with balances conserved.

Scale is set in `CoreBankDemo.LoadTests/appsettings.json` (`TransactionCount`, `VuCount`).
LoadTestSupport also exposes an **MCP server** at `http://localhost:5181/` for agent-driven
orchestration — see `mcp-config.example.json` and
[CoreBankDemo.LoadTestSupport/README.md](CoreBankDemo.LoadTestSupport/README.md). Full details in
[CoreBankDemo.LoadTests/README.md](CoreBankDemo.LoadTests/README.md).

## Security note

The local topologies use fixed, non-secret credentials (for example the Redis password
`myredispassword123` in `dapr/components*/pubsub-redis.yaml` and the AppHosts). These containers are
disposable and local-only, spun up and torn down by Aspire. They are not secrets and must never be
reused anywhere real.

## Troubleshooting

**Build fails with `dotnet tool run kiota generate ... exited with code 1`**
Run `dotnet tool restore` — the Payments API generates its CoreBank client from the checked-in
OpenAPI contract at build time.

**The AppHost won't start / `devproxy` not found**
Either install Dev Proxy 3.2.0 on `PATH` or start with `aspire run -- --Features:UseDevProxy=false`.

**No traces in Jaeger**
Check that the `jaeger` container is running in the Aspire Dashboard; the OTLP endpoint is resolved
from Aspire and injected as `JAEGER_OTLP_ENDPOINT`, so it is never a hardcoded port.

**Ports already in use** (5294, 5032, 8000, 15888, 16686, 5432, 6379)
The PostgreSQL, Redis and Jaeger containers use `ContainerLifetime.Persistent` and survive an
AppHost stop by design. Stop the stragglers, or reset state by removing those containers.

**Payments stay `Pending`**
Look at the Payments API outbox processor logs in the dashboard. A high Dev Proxy error rate, a
stopped Core Bank API, or a missing Redis lease will all park messages until the downstream returns;
after 5 failed attempts a message goes terminally `Failed`.

## Documentation

- **[ARCHITECTURE.md](ARCHITECTURE.md)** — component detail, database schemas, data flow
- **[docs/adr/](docs/adr/)** — 19 accepted decision records; these govern the system's behavior
- **[docs/bmad/](docs/bmad/)** — planning, implementation and test artifacts, plus
  `constraints.md` (the binding invariants, external API surface, ports and test rules)
- **[AGENTS.md](AGENTS.md)** — orientation for AI agents working in this repository

## Further reading

- [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/)
- [Resilience in .NET](https://learn.microsoft.com/dotnet/core/resilience/)
- [Transactional Outbox pattern](https://microservices.io/patterns/data/transactional-outbox.html)
- [Idempotent Consumer pattern](https://microservices.io/patterns/communication-style/idempotent-consumer.html)
- [Dapr](https://dapr.io/)
- [Dev Proxy](https://learn.microsoft.com/microsoft-cloud/dev/dev-proxy/)
- [OpenTelemetry .NET](https://opentelemetry.io/docs/languages/net/)
