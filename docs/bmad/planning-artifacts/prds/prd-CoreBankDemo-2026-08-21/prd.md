---
title: "PRD: CoreBankDemo Rebuild"
status: final
created: 2026-08-21
updated: 2026-08-28
---

# PRD: CoreBankDemo Rebuild

## 0. Document Purpose

Defines the requirements for rebuilding CoreBankDemo from scratch on branch `feature/bmad`: the same externally observable system that exists on `main`, re-implemented story-driven with a first-class unit-test suite. Consumed downstream by `bmad-architecture` and `bmad-create-epics-and-stories`. The binding technical contract (invariants, endpoint surface, conventions, cruft rulings A1–A8) lives in `docs/bmad/constraints.md`; this PRD does not restate what that contract fixes — it defines the capabilities and quality bar.

## 1. Vision

A conference-demo distributed banking system whose *code, tests, and stories are the product*: every resilience pattern (Outbox, Inbox, partitioned ordering, retries, circuit breaking, distributed tracing) is implemented cleanly, covered by fast unit tests, traceable to a story, and verifiable end-to-end by an invariant-asserting load test — demonstrating both the patterns themselves and BMAD-driven AI development on a non-trivial brownfield rebuild.

## 2. Target User

- **Loek (owner/speaker):** runs the demo live; needs one-command startup, green gates, explainable code, and a story/test trail that narrates well on stage.
- **Conference audience & repo visitors:** engineers who read the repo afterwards; they judge it by clarity of pattern implementations, honesty of tests, and coherence of docs.

Jobs to be done: (a) demonstrate resilience patterns credibly; (b) demonstrate the BMAD process credibly; (c) regain a second-scale feedback loop (unit tests) below the minute-scale load test.

## 3. Glossary

| Term | Meaning |
|---|---|
| **Payment** | A transfer request accepted by PaymentsAPI (from-account, to-account, amount, currency, idempotency key). |
| **Transaction** | A payment being executed against the ledger in CoreBankAPI. |
| **Idempotency key** | Client-supplied (or generated) unique key; the dedupe identity of a payment across all hops. |
| **Outbox / Inbox** | Store-and-forward tables enabling reliable sending / idempotent receiving; processed by background pollers. |
| **Partition** | FNV-1a hash bucket of the idempotency key; unit of ordering and distributed locking. |
| **Drain** | State where all message stores hold only terminal-status rows. |
| **Invariants** | The five system properties in constraints.md §1 (exactly-once, no loss, balance conservation, terminal completeness, per-key ordering). |

## 4. Features

### 4.1 Payment intake (PaymentsAPI)

Accepts payments over HTTP and acknowledges them durably before any downstream work happens. Realizes the external contract in constraints.md §2.

- **FR-1:** `POST /api/payments` validates the request (accounts, amount, currency) and returns all validation errors at once on rejection.
- **FR-2:** An accepted payment is stored in the Payments Outbox and acknowledged `202 Accepted` with a pending status; acceptance implies eventual processing (no loss).
- **FR-3:** The `Idempotency-Key` header identifies the payment; absent a header, a key is generated. A duplicate key returns `202` referencing the existing record — never a second Outbox row.
- **FR-4:** Each stored message carries its partition id (derived from the idempotency key) and captured trace context.

### 4.2 Reliable forwarding (Payments Outbox processing)

- **FR-5:** A background processor polls the Outbox and forwards each payment to CoreBankAPI over HTTP (destination-account validation, then transaction submission).
- **FR-6:** Processing is partitioned: per-partition distributed lock, oldest-first within a partition, partitions processed concurrently — preserving per-key order while scaling out.
- **FR-7:** A failed forward returns the message to pending with an incremented retry count and recorded error; after the retry limit the message is terminal `Failed`. Stuck `Processing` rows older than the processing timeout are reclaimed.
- **FR-8:** Forwarding restores the stored trace context so one payment forms one distributed trace. The CoreBank HTTP integration is generated during build with Kiota from a checked-in OpenAPI document covering every public CoreBankAPI operation; generated sources are not committed and remain behind the application-owned `ICoreBankApiClient` port.

### 4.3 Idempotent transaction processing (CoreBankAPI)

- **FR-9:** `POST /api/transactions/process` validates, dedupes by transaction id (the idempotency key), stores an Inbox row, and returns `202`. Duplicates replay the cached response or report current status — business logic never executes twice for one key.
- **FR-10:** `GET /api/transactions/{idempotencyKey}` reports transaction status.
- **FR-11:** An Inbox processor executes each transaction atomically: load and lock both accounts, validate (existence, active, sufficient funds), apply debit and credit, cache the response on the Inbox row, and enqueue resulting domain events — all in one database transaction.
- **FR-12:** Validation failures produce a terminal failed transaction with a cached failure response (also replayed to duplicates); they do not move money and do not count as message loss.

### 4.4 Account services (CoreBankAPI)

- **FR-13:** `POST /api/accounts/validate` reports whether an account exists and is active; `GET /api/accounts/{accountNumber}` returns account details.
- **FR-14:** Demo accounts are seeded idempotently at startup (3 demo accounts; 10 load-test accounts at €10M each via LoadTestSupport).

### 4.5 Domain event publishing (CoreBankAPI → Dapr)

- **FR-15:** Each executed transaction enqueues `TransactionCompleted` (or `TransactionFailed`) plus one `BalanceUpdated` per affected account, written transactionally with the ledger change (no publish outside the transaction).
- **FR-16:** A messaging-outbox processor publishes the enqueued events as CloudEvents to Dapr pubsub `pubsub`, topic `transaction-events`, with correct CloudEvent type constants and propagated trace context. It uses the same partition/lock/retry machinery as all other processors (constraints ruling A2).

### 4.6 Event consumption (PaymentsAPI)

- **FR-17:** PaymentsAPI subscribes to `transaction-events` (declarative Dapr subscription routed by event type) and stores each event idempotently in its Inbox; duplicate deliveries are dropped without error.
- **FR-18:** An Inbox processor dispatches events by type to handlers (current behavior: structured logging + span tagging), preserving trace context.

### 4.7 Shared messaging library

- **FR-19:** Inbox/Outbox behavior (idempotent store, partitioned polling, distributed locking, batch claiming, retry/poison handling, trace-context restoration, status constants) is implemented once in a shared library and reused by every processor — no service re-implements the pattern.
- **FR-20:** The library exposes seams (interfaces) for locking, publishing, persistence, and time so all pattern logic is unit-testable without infrastructure.

### 4.8 Orchestration & chaos (AppHost)

- **FR-21:** One command boots the full system via Aspire: Postgres (`paymentsdb`, `corebankdb`), Redis, Dapr pub/sub components with one adapter per logical API service, Jaeger, two PaymentsAPI replicas, two CoreBankAPI replicas, pgAdmin/RedisInsight. Both regular and load-test AppHosts use this replicated application topology by default; both CoreBankAPI replicas publish through the logical CoreBank adapter, the logical Payments adapter delivers through the stable Aspire-proxied PaymentsAPI endpoint, and neither adapter is claimed as infrastructure high availability.
- **FR-22:** DevProxy fault injection (errors, latency, rate limiting) is available opt-in for the resilience demo stages; the HTTP layer's retry/circuit-breaker/timeout policies handle transient faults.
- **FR-23:** Configuration matches documentation: partition count 4, no dead feature flags (constraints rulings A1, A3). Competing API instances use the same distributed partition locks so no partition is processed concurrently or out of order, while different partitions remain eligible for parallel processing.

### 4.9 Load-test & assertion harness (LoadTestSupport + k6)

- **FR-24:** A LoadTests AppHost runs k6 through the stable PaymentsAPI proxy endpoint against the default two-by-two API topology (configurable transaction count / VUs, ~10% deliberate duplicate keys) using disposable infrastructure.
- **FR-25:** LoadTestSupport exposes reset, drain-polling, and assertion endpoints plus the equivalent MCP tools (`reset_database`, `poll_until_drained`, `get_assertion_results`, inbox/outbox inspection) validating all five invariants.
- **FR-26:** The harness conforms to the rebuilt schemas (it adapts to the code, not vice versa) while preserving assertion semantics.

### 4.10 Unit-test suite & coverage gate (first-class deliverable)

- **FR-27:** Every logic project has an xUnit test project (AwesomeAssertions, Moq); tests are written test-first per story.
- **FR-28:** Plain `dotnet test` on the rebuild solution filter enforces ≥90% line coverage on logic projects via coverlet — locally, no CI required. Hosting boilerplate is excluded via attributes/filters.
- **FR-29:** Pattern contracts (idempotent store, retry/poison state machine, partition assignment, cross-instance exclusivity and ordering, dedupe/replay, balance arithmetic, validation rules) each have explicit, named tests — including generated-client compilation and adapter tests for the Kiota boundary — so the suite reads as documentation of the patterns.

## 5. Non-Functional Requirements

- **NFR-1 Invariants:** the five constraints.md §1 invariants hold under concurrent load with fault injection and competing service replicas; verified by the acceptance harness.
- **NFR-2 Traceability:** one payment = one trace across HTTP hop, message stores, and Dapr hop (Jaeger-verifiable).
- **NFR-3 Conventions:** constraints.md §3 conventions are binding (EnsureCreated-only, TimeProvider, thin controllers, validated options, structured logging, central package management).
- **NFR-4 Process traceability:** every production change traces to a BMAD story; one commit per story.
- **NFR-5 Demo ergonomics:** cold start to a healthy replicated system with one command; existing `.http` demo flows use a stable PaymentsAPI ingress and work unchanged.

## 6. Non-Goals (Explicit)

Production deployment; new features or behavior changes beyond A1–A8 rulings; authentication/authorization; EF migrations; alternative brokers/databases; performance targets beyond load-test pass; UI.

## 7. Scope

**In:** rebuild of Messaging, ServiceDefaults, CoreBankAPI, PaymentsAPI, AppHost; LoadTestSupport/k6 realignment; test infrastructure; regenerated ARCHITECTURE.md + ADRs for A1–A8.
**Out:** everything in §6; changes to the external contract; the `.claude` project skills except where base-class surfaces change (then updated in the docs epic).

## 8. Success Metrics

1. Full load-test run green: all five invariants pass with 10% duplicate keys (counter-metric: zero assertion relaxations without an ADR).
2. Coverage gate ≥90% line on logic projects, enforced by plain `dotnet test` (counter-metric: no meaningless assertion-free tests — TEA epic-end reviews check).
3. 100% of rebuilt production commits reference a story ID.
4. Regenerated ARCHITECTURE.md contains no references to non-existent code.
5. One-command boot + unchanged `demo-requests.http` flows.

## 9. Open Questions

None blocking. Deferred to architecture: exact seam shapes for A6, lock-renewal ruling for A4 (wire or delete).

## 10. Assumptions Index

- [ASSUMPTION] Event handlers in PaymentsAPI remain log-and-tag only (no local state mutation), matching `main`.
- [ASSUMPTION] The 3 demo accounts and 10 load-test accounts (identities and balances) carry over unchanged.
- [ASSUMPTION] k6 script parameters (count/VUs/duplicate ratio) carry over unchanged.
