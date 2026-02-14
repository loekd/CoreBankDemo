# Core Bank Demo - Conference Talk Guide

**Duration:** 55 minutes
**Topic:** Building resilient mission-critical systems in banking

## Prerequisites

**Using Aspire (Recommended):**
1. Start Aspire: `cd CoreBankDemo.AppHost && dotnet run`
2. Aspire Dashboard: http://localhost:15888
3. For chaos testing: `dotnet tool restore && dotnet devproxy --config-file devproxy.json`

**Manual start (alternative):**
1. Start infrastructure: `docker compose up -d`
2. Restore tools: `dotnet tool restore`
3. Start Core Bank API: `cd CoreBankDemo.CoreBankAPI && dotnet run`
4. Start Payments API: `cd CoreBankDemo.PaymentsAPI && dotnet run`

## Demo Flow

### Stage 0: Baseline (5 min)
**Goal:** Show the basic setup - PaymentsAPI calling legacy CoreBankAPI

**What to show:**
- Architecture: Payments API → Core Bank API (REST)
- Two endpoints: validate account, process transaction
- Show successful payment via `demo-requests.http`

**Key Points:**
- Legacy core banking system (simulated as SaaS)
- No security (OAuth) - not our focus
- Works perfectly... when everything is perfect

---

### Stage 1: Short Outages - Retry & Circuit Breaker (10 min)
**Goal:** Handle transient failures

**Demo Steps:**
1. Start DevProxy: `dotnet devproxy --config-file devproxy.json`
2. Enable `GenericRandomErrorPlugin` in `devproxy.json` (set `enabled: true`)
3. Show failures happening
4. Show existing resilience: `AddStandardResilienceHandler()` in Program.cs:12
5. Show retries working via logs

**What to explain:**
- Standard resilience handler includes:
  - Retry with exponential backoff
  - Circuit breaker
  - Timeout policies
- Show in Jaeger: http://localhost:16686
  - Multiple spans for retries
  - Latency measurements

**Key Points:**
- Handles ~95% of real-world issues
- Fast recovery from hiccups
- But what about longer outages?

---

### Stage 2: Longer Outages - Outbox Pattern (15 min)
**Goal:** Handle sustained outages without losing data

**Demo Steps:**
1. Enable longer outages in DevProxy (increase error rate)
2. Show payments failing
3. Introduce Outbox pattern
4. Show payments queued locally
5. Show automatic processing when Core Bank recovers

**Implementation:**
- Add SQLite for outbox table
- Store failed payments locally
- Background service polls and retries
- Show database table via logs/UI

**Key Points:**
- Don't lose customer requests
- Eventually consistent
- But introduces new problems...

---

### Stage 3: Idempotency - Inbox Pattern (10 min)
**Goal:** Prevent duplicate processing

**Demo Steps:**
1. Show problem: retry can cause duplicate charges
2. Show Core Bank receiving same transaction twice
3. Add Inbox pattern to Core Bank API
4. Demonstrate idempotency key handling
5. Show deduplication working

**Implementation:**
- Track processed transaction IDs
- Check before processing
- Return original result for duplicates

**Key Points:**
- Critical for financial systems
- Idempotency keys
- Both sides need to cooperate

---

### Stage 4: Message Ordering - Sessions (10 min)
**Goal:** Handle ordering issues with competing consumers

**Demo Steps:**
1. Show problem: multiple payments from same account processed out of order
2. Add sessions/partitioning by account number
3. Show ordered processing per account
4. Show concurrent processing across accounts

**Implementation:**
- Partition outbox by account
- Single consumer per account
- Multiple consumers for different accounts

**Key Points:**
- Balance concurrency with ordering
- Per-entity ordering
- Scale out while maintaining guarantees

---

### Stage 5: Tooling & Wrap-up (5 min)
**Goal:** Show how tooling helps ship faster

**What to show:**
- DevProxy for chaos testing
- Jaeger for observability
- DevContainer for consistent environments
- Aspire/Dapr (mention, don't deep dive)

**Key Takeaways:**
1. Resilience is layered:
   - Retry/CB for short failures
   - Outbox for longer failures
   - Inbox for idempotency
   - Sessions for ordering
2. Observability is critical
3. Test failure scenarios in dev
4. Tools exist to help

---

## Quick Start Commands

```bash
# Using Aspire (Recommended)
cd CoreBankDemo.AppHost
dotnet run

# View Aspire Dashboard
open http://localhost:15888

# For chaos testing (Terminal 2)
dotnet tool restore
dotnet devproxy --config-file devproxy.json
```

## Useful URLs

- **Aspire Dashboard:** http://localhost:15888 (shows all services, logs, traces, metrics)
- **Jaeger UI:** http://localhost:16686
- **Payments API:** http://localhost:5294/openapi/v1.json
- **Core Bank API:** http://localhost:5032/openapi/v1.json
- **DevProxy:** http://localhost:8000 (proxy port)
