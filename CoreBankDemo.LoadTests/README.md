# Load Tests

Verifies that the CoreBankDemo system processes every payment transaction **exactly once** under concurrent load.

## How it works

```
┌─────────────────────────────────────────────────────┐
│  CoreBankDemo.LoadTests  (Aspire AppHost)            │
│                                                      │
│  Postgres (disposable) ──► paymentsdb                │
│                        └──► corebankdb  (+ 10 seeded │
│                                           accounts)  │
│  Redis (disposable, port 6381)                       │
│  Dapr (components-loadtest/)                         │
│                                                      │
│  PaymentsAPI  ──────────────────────────────────┐   │
│  CoreBankAPI  ──────────────────────────────┐   │   │
│  LoadTestSupport  (assert API) ─────────┐   │   │   │
│                                         │   │   │   │
│  k6 container ──────────────────────────┘───┘───┘   │
└─────────────────────────────────────────────────────┘
```

### Test flow

1. **Setup** — k6 checks both APIs are healthy.
2. **Load** — 10 VUs (configurable) race to submit `N` unique payments to the Payments API. ~10% are deliberate retries using the same idempotency key to prove deduplication.
3. **Drain** — k6 polls `GET /assert/drain` every 3 seconds until all inbox messages are processed (or 5-minute timeout).
4. **Assert** — k6 calls `GET /assert/results` and runs checks:

| Check | Pass condition |
|---|---|
| `no failed inbox messages` | Zero `Failed` inbox messages |
| `no pending inbox messages` | Zero `Pending`/`Processing` inbox messages |
| `no duplicate processing` | No idempotency key processed more than once |
| `all submitted transactions processed` | Inbox completed count == outbox submitted count |

## Running

```bash
dotnet run --project CoreBankDemo.LoadTests
```

## Configuration

Edit `appsettings.json` to change scale:

```json
"LoadTest": {
  "TransactionCount": "1000",
  "VuCount": "10"
}
```

All infrastructure is **disposable** — Postgres and Redis are torn down when Aspire exits. No cleanup needed.

## Seed data

Ten load-test accounts (`NL01LOAD0000000001` → `NL10LOAD0000000010`) with €10,000,000 balance are inserted by **LoadTestSupport** on startup. It waits for CoreBankAPI to be healthy first (schema guaranteed), then inserts the accounts idempotently. Aspire creates the databases; EF's `EnsureCreated()` in CoreBankAPI creates all tables.

