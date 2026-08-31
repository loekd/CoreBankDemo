# Story 7.3 Acceptance Evidence

## Configuration

- Date: 2026-08-31
- Baseline commit: `303ac0f9e8dce0b13adfc7d859d22f877f49c3d1`
- AppHost: `CoreBankDemo.LoadTests` only
- Transactions: 100 unique plus 10 deliberate duplicate submissions
- Virtual users: 4
- PostgreSQL integration image: `postgres:18.3`
- AppHost generation start: `2026-08-31T16:01:16Z`
- k6 container run window: `2026-08-31T16:01:41.974Z` to `2026-08-31T16:01:45.378Z`

## Environment Fixes Required for This Sandbox

Two prior blockers (Aspire DCP's loopback IPv6 control endpoint, and a stale `aspire`
CLI installation) were already resolved before this session. Reaching a green
distributed run required three additional, environment-scoped fixes — none of which
change banking behavior, message-store semantics, or the frozen spec boundaries:

1. **`host.docker.internal` is a dead end for containers reaching this sandbox.**
   Proven with a throwaway `0.0.0.0`-bound probe HTTP server on the sandbox that a
   sibling Docker container could never reach via `host.docker.internal`, regardless
   of what interface the target process bound to. This sandbox nests its own Docker
   daemon; `host.docker.internal` does not route back to it.
   **Fix:** rewired the `k6` container resource in `CoreBankDemo.LoadTests/AppHost.cs`
   to reference `paymentsApi.GetEndpoint("http")` and
   `loadTestSupport.GetEndpoint("load-test")` via `WithEnvironment(...)` instead of
   hardcoded `http://host.docker.internal:*` strings. This is Aspire 13.3+'s built-in
   **container tunnel** — the documented, cross-platform (Docker Desktop and native
   Linux alike) replacement for `host.docker.internal`. Confirmed live: k6 connected
   via `http://aspire.dev.internal:<tunnel-port>` and the tunnel proxy container
   (`aspire-container-network-tunnelproxy-*`) was observed running.
2. **PaymentsAPI/CoreBankAPI/LoadTestSupport were loopback-only.** Even with the
   tunnel, DCP still needs to reach the target process from the sandbox's own network
   stack; a secondary check (`--network=host` container probe) confirmed loopback
   itself was reachable, so this was addressed by adding a new `loadtest` launch
   profile (`0.0.0.0` binding, same ports) to the three projects'
   `Properties/launchSettings.json`, selected only by
   `CoreBankDemo.LoadTests/AppHost.cs` via `launchProfileName: "loadtest"`. The
   existing `http`/`https` profiles — including PaymentsAPI's `127.0.0.1` binding
   that DevProxy's chaos-injection plugin and
   `NoDaprServiceInvocationArchitectureTests.AppHost_keeps_two_replicas_behind_one_logical_dapr_adapter_per_api`
   depend on — are untouched, so the regular `CoreBankDemo.AppHost` + DevProxy flow
   is unaffected.
3. **A pre-existing, unrelated leftover container (`dapr_redis`, created
   2026-08-29, not part of any AppHost) was squatting on host port 6379.** Because
   the LoadTests AppHost's Redis resource uses a fixed host port
   (`.WithHostPort(6379)`), every app process was silently talking to this stale
   Redis instead of a fresh one, which made the one-shot processor-release gate
   (`RedisProcessorStartGate`) appear to persist "already released" state across
   AppHost restarts. **Fix:** stopped (not removed) the stale container; the
   AppHost's own Redis container then bound port 6379 correctly.

## Rebuild Gate

Command: `dotnet test CoreBankDemo.Rebuild.slnf`

- Result: passed
- Total: 848 passed, 0 failed, 1 skipped (pre-existing real-Redis test in
  `CoreBankDemo.ServiceDefaults.Tests`)
- Per-project: DemoRunner.Tests 120/120, LoadTestInitializer.Tests 6/6,
  ServiceDefaults.Tests 153/154 (1 skipped), CoreBankAPI.Tests 89/89,
  Messaging.Tests 125/125, PaymentsAPI.Tests 138/138, LoadTestSupport.Tests 32/32,
  Persistence.IntegrationTests 185/185 (includes both replicated CoreBank
  Inbox and Outbox ordering/exclusivity/cross-partition-concurrency tests)
- Coverage: every measured module passed the 90% line-coverage gate (lowest:
  CoreBankAPI unit tier 91.04% line); Persistence aggregate line coverage 98.55%
- `git diff --check`: passed (no whitespace errors)

## Distributed Run

- AppHost build: passed with 0 errors
- Resource startup: all healthy — 2× `corebank-api`, 2× `payments-api`,
  `loadtest-support`, `postgres`, `redis`, `jaeger`, both Dapr sidecars, `pubsub`
- Reset initializer: completed successfully — `POST /reset` returned 200 on the
  first attempt; `ResetResponseValidator` accepted the response
  (`AccountsReset=10`, `InitialBalancePerAccount=10,000,000`,
  `TotalBalance=100,000,000`)
- k6: container exited **0**
- k6 thresholds: `checks: rate==1` → 100.00%; `http_req_duration{type:payment}:
  p(95)<2000` → p(95)=24.59ms; `http_req_failed: rate<0.01` → 0.00%
- k6 checks: 673/673 succeeded (100%), 0 failed

### REST / MCP Assertion Parity

Both calls targeted the same completed run (`expectedUnique=100`) and were
compared programmatically — **field-for-field identical**:

```json
{
  "allPassed": true,
  "checks": {
    "noFailedMessages": {"passed": true, "detail": "0 failed message(s); Failed: PaymentsOutbox=0, CoreBankInbox=0, CoreBankOutbox=0, PaymentsInbox=0"},
    "noPendingMessages": {"passed": true, "detail": "0 still pending/processing; NonTerminal: PaymentsOutbox=0, CoreBankInbox=0, CoreBankOutbox=0, PaymentsInbox=0"},
    "noDuplicateProcessing": {"passed": true, "detail": "No duplicates", "duplicates": []},
    "expectedUniqueProcessed": {"passed": true, "detail": "ExpectedUnique=100, CompletedUnique=100"},
    "allSubmittedProcessed": {"passed": true, "detail": "OutboxTotal=100, InboxCompleted=100"},
    "balanceConservation": {"passed": true, "detail": "Total=100000000.00, Expected=100000000.00"},
    "balancesCorrect": {"passed": true, "detail": "All balances match expected values", "discrepancies": []},
    "stageCardinality": {
      "passed": true,
      "detail": "Expected N/N/3N/3N=100/100/300/300; Actual=100/100/300/300",
      "paymentsOutbox": {"total": 100, "completed": 100, "failed": 0, "nonTerminal": 0},
      "coreBankInbox": {"total": 100, "completed": 100, "failed": 0, "nonTerminal": 0},
      "coreBankOutbox": {"total": 300, "completed": 300, "failed": 0, "nonTerminal": 0},
      "paymentsInbox": {"total": 300, "completed": 300, "failed": 0, "nonTerminal": 0}
    },
    "canonicalAccountSet": {"passed": true, "detail": "Expected 10 canonical accounts; actual=10, missing=0, unexpected=0", "missing": [], "unexpected": []}
  }
}
```

Row counts (all four stores, zero `Pending`/`Processing`/`Failed`):

| Store | Total | Completed |
|---|---|---|
| Payments Outbox | 100 | 100 |
| CoreBank Inbox | 100 | 100 |
| CoreBank Messaging Outbox | 300 | 300 |
| Payments Inbox | 300 | 300 |

The 10 canonical LOAD accounts (`NL01LOAD0000000001`…`NL10LOAD0000000010`) matched
exactly (0 missing, 0 unexpected) and balance conservation held
(`100,000,000.00` total, matching expected).

## Trace and Ordering Evidence

`opentelemetry-mcp` failed to connect at session start (`uvx` not on `PATH`); fixed
mid-session (symlinked `uvx` → the installed `uv` binary, which self-dispatches on
argv[0]) for future sessions, but this session queried Jaeger's REST API directly
(`http://localhost:16686/api/*`) instead of restarting the MCP connection.

- **Service list**: `CoreBank.PaymentsAPI`, `CoreBank.CoreBankAPI`,
  `payments-api-dapr-cli`, `corebank-api-dapr-cli` all present.
- **Representative trace** `bab5f85c8592eae6e5ef9a25783f5010` (47 spans, one
  duplicate-submission's full lifecycle) shows the complete chain under a single
  trace ID:
  `POST api/Payments` (PaymentsAPI, server) → `ProcessOutboxMessage`
  (PaymentsOutbox, partition 0) → `POST` client → `POST api/Transactions/process`
  (CoreBankAPI, server) → `POST api/Accounts/validate` → `ProcessInboxMessage`
  (CoreBankInbox, partition 0) → `ProcessOutboxMessage` × 3 (CoreBankOutbox,
  partitions 0/1/3) → `dapr.proto.runtime.v1.Dapr/PublishEvent` →
  `pubsub/transaction-events` → `POST events/transactions/completed` and
  `POST events/transactions/balance-updated` (PaymentsAPI, server) →
  `ProcessInboxMessage` × 3 (PaymentsInbox, partition 0).
  Both the HTTP hop (Payments→CoreBank) and the Dapr hop (CoreBank→pubsub→Payments)
  preserved the same trace ID end-to-end, confirming `traceparent` propagation;
  combined with the passing `IEventPublisherSignatureTests`/`DaprEventPublisherTests`
  (ADR-017 `cloudevent.tracestate` metadata propagation) this satisfies the
  two-hop context-propagation criterion.
- **Both replicas participated**: PaymentsAPI `POST api/Payments` split
  82/28 across its two `service.instance.id`s; CoreBankAPI
  `POST api/Transactions/process` split 16/84 across its two instances.
- **Errors** (scoped to the run window, `16:01:40Z`–`16:01:50Z`): 10 on PaymentsAPI,
  0 on CoreBankAPI. All 10 are `Npgsql.PostgresException 23505` (duplicate key on
  `IX_OutboxMessages_IdempotencyKey`) — the expected, handled idempotency-conflict
  path for the 10 deliberate duplicate submissions, not defects.
- **Slow traces** (>500ms) in the run window: 0 for both services.
- **Ordering/exclusivity (live spans)**: collected all 800 tagged
  `ProcessInboxMessage`/`ProcessOutboxMessage` spans (100 unique traces) in the run
  window, grouped by `(messaging.store.name, PartitionId)` — 16 groups
  (4 stores × 4 partitions) — and checked every consecutive pair's
  `[start, start+duration)` interval per group. **Zero overlaps** across all
  16 groups. Combined with the passing replicated PostgreSQL/Redis
  `ReplicatedCoreBankInboxProcessorTests`/`ReplicatedCoreBankOutboxProcessorTests`,
  this is the user-approved proof for the fifth invariant.

## Failure Classification

None. All checks passed; no banking invariant failed and no code defect was found.
The three environment fixes above were required to make the disposable topology
reachable inside this specific sandbox and do not alter application behavior.

## Final Verdict

**Accepted.**
