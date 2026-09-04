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

1. **Initialize** — the one-shot initializer resets both databases, validates all 10 account balances/counts, and releases both API processor gates. Invalid reset semantics prevent k6 from starting.
2. **Setup** — k6 checks both APIs are healthy.
3. **Load** — 10 VUs (configurable) race to submit `N` unique payments to the Payments API. ~10% are deliberate retries using the same idempotency key to prove deduplication.
4. **Drain** — k6 polls all four message stores through `GET /assert/drain` every 500ms.
5. **State gate** — k6 calls `GET /assert/results?expectedUnique=<transactionCount>`. Every named check has a fail-closed threshold, so setup, endpoint, malformed JSON, drain, and invariant failures exit non-zero.
6. **Final verdict** — compare REST/MCP JSON, run replicated Tier-2 Inbox/Outbox ordering tests, and analyze exact-window spans. Trace/order evidence is required in addition to k6's state gate.

| Check | Pass condition |
|---|---|
| `no failed inbox messages` | Zero `Failed` inbox messages |
| `no pending inbox messages` | Zero `Pending`/`Processing` inbox messages |
| `no duplicate processing` | No idempotency key processed more than once |
| `expected unique count processed` | Completed unique idempotency keys == configured transaction count |
| `all submitted transactions processed` | Inbox completed count == outbox submitted count |
| `stage cardinality N/N/3N/3N` | Payments Outbox/CoreBank Inbox/CoreBank Outbox/Payments Inbox completed rows equal N/N/3N/3N |
| `canonical account set exact` | Exactly the 10 seeded load-test accounts exist |
| `no failed/non-terminal messages` | Every one of the four stores is terminal and successful |

## Running

Use `aspire start --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --non-interactive`. This is the only AppHost for the acceptance run; do not start `CoreBankDemo.AppHost`.

## Configuration

Edit `appsettings.json` to change scale:

```json
"LoadTest": {
  "TransactionCount": "1000",
  "VuCount": "10"
}
```

PaymentsAPI calls CoreBankAPI over HTTP through the generated Kiota client; Dapr is reserved for CoreBank-to-Payments events. DevProxy is outside Story 7.3's first full acceptance gate.

All infrastructure is **disposable** — Postgres and Redis are torn down when Aspire exits. No cleanup needed.

## Seed data

Ten load-test accounts (`NL01LOAD0000000001` → `NL10LOAD0000000010`) with €10,000,000 balance are inserted by **LoadTestSupport** on startup. It waits for CoreBankAPI to be healthy first (schema guaranteed), then inserts the accounts idempotently. Aspire creates the databases; EF's `EnsureCreated()` in CoreBankAPI creates all tables.
