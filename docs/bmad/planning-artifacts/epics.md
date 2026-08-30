---
stepsCompleted: [1, 2, 3, 4]
inputDocuments:
  - docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-08-21/prd.md
  - docs/bmad/planning-artifacts/architecture/architecture-CoreBankDemo-2026-08-21/ARCHITECTURE-SPINE.md
  - docs/bmad/constraints.md
  - docs/bmad/planning-artifacts/briefs/brief-CoreBankDemo-2026-08-21/addendum.md
status: final
created: 2026-08-21
---

# CoreBankDemo - Epic Breakdown

## Overview

Complete epic and story breakdown for the CoreBankDemo rebuild, decomposing the PRD (FR-1..FR-29, NFR-1..NFR-5) and the architecture spine (AD-1..AD-13) into implementable, TDD-ready stories. Epic order is fixed (brief addendum): each epic enters `CoreBankDemo.Rebuild.slnf` at its start after demolition of the old sources (AD-10). Every story: failing tests first, then implementation; gate = `dotnet test CoreBankDemo.Rebuild.slnf` with the ≥90% coverlet threshold.

## Requirements Inventory

### Functional Requirements

FR-1..FR-29 as defined in the PRD §4 (payment intake 1–4; reliable forwarding 5–8; idempotent processing 9–12; account services 13–14; event publishing 15–16; event consumption 17–18; messaging kernel 19–20; orchestration 21–23; load harness 24–26; test suite 27–29). The PRD is the authoritative text; stories cite FR IDs.

### Non-Functional Requirements

NFR-1 invariants under load · NFR-2 one payment = one trace · NFR-3 constraints.md conventions binding · NFR-4 story/commit traceability · NFR-5 one-command demo ergonomics.

### Additional Requirements (Architecture)

- AD-2 hexagonal seams; AD-3 kernel-owned processor machinery with `IOutboxDeliveryStrategy` and cross-instance partition exclusivity; AD-4 identity split (ordering key vs per-store dedupe); AD-5 atomic state+events; AD-6 fixed port set with a Kiota-backed CoreBank adapter; AD-7 amended by ADR-011 to use renewable direct-Redis locks; AD-8 trace persistence; AD-9 three test tiers + VSTest mode; AD-10 slnf gate; AD-11 delivery outcome contract (business rejection = Completed + failure payload); AD-12 wire contracts frozen below and represented by a checked-in CoreBank OpenAPI document; AD-13 two local replicas per API behind stable Aspire ingress.
- No starter template — brownfield rebuild in an existing solution.

### UX Design Requirements

The banking product remains API-only. Story 7.4 adds a local presentation-tool TUI for the speaker;
it is not a banking UI and must remain outside the banking services. ADR-015 must record that narrow
exception to the PRD's broad UI non-goal before implementation begins.

## Wire Contracts (frozen per AD-12, extracted from `main` before demolition)

Event payload records (live once in ServiceDefaults `CloudEventTypes`; CloudEvent types `com.corebank.transaction.completed` / `com.corebank.transaction.failed` / `com.corebank.account.balance.updated`):

```csharp
public record TransactionCompletedEvent(string TransactionId, string Status, DateTimeOffset ProcessedAt);
public record TransactionFailedEvent(string TransactionId, string Status, DateTimeOffset ProcessedAt, string? ErrorReason);
public record BalanceUpdatedEvent(string TransactionId, string AccountNumber, decimal Delta, decimal NewBalance, string Currency);
```

PaymentsAPI DTOs:

```csharp
public record PaymentRequest(string FromAccount, string ToAccount, decimal Amount, string Currency);
// validation: accounts 15–34 chars; amount 0.01–1,000,000; currency ^[A-Z]{3}$; all Required
public record PaymentResponse(string PaymentId, string TransactionId, string Status, decimal Amount, string Currency, DateTimeOffset ProcessedAt);
public record TransactionResponse(string TransactionId, string Status, DateTimeOffset ProcessedAt);
public record AccountValidationResponse(string AccountNumber, bool IsValid, string? AccountHolderName = null, decimal? Balance = null);
```

CoreBankAPI DTOs:

```csharp
public record TransactionRequest(string FromAccount, string ToAccount, decimal Amount, string Currency, string TransactionId);
// validation: as PaymentRequest + TransactionId Required, 1–100 chars
public record TransactionResponse(string TransactionId, string Status, DateTimeOffset ProcessedAt);
public record AccountValidationRequest(string AccountNumber); // Required, 15–34 chars
public record AccountValidationResponse(string AccountNumber, bool IsValid, string? AccountHolderName = null, decimal? Balance = null);
public record AccountDetailsResponse(string AccountNumber, string AccountHolderName, decimal Balance, string Currency, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);
public record BalanceUpdatedResponse(string TransactionId, string AccountNumber, decimal Delta, decimal NewBalance, string Currency);
```

### FR Coverage Map

| FR | Stories | FR | Stories |
|---|---|---|---|
| FR-1 | 5.2 | FR-16 | 4.7 |
| FR-2 | 5.1, 5.2 | FR-17 | 5.5 |
| FR-3 | 5.1, 5.2 | FR-18 | 5.6 |
| FR-4 | 5.1, 2.1 | FR-19 | 2.1–2.6 |
| FR-5 | 5.4 | FR-20 | 2.4, 2.5, 3.2, 3.3, 6.2 |
| FR-6 | 2.4, 5.4, 6.3 | FR-21 | 6.1, 6.2, 6.3 |
| FR-7 | 2.3, 2.6 | FR-22 | 6.4 |
| FR-8 | 2.4, 5.3 | FR-23 | 6.1, 6.2, 6.3, 3.1 |
| FR-9 | 4.4 | FR-24 | 6.3, 7.3 |
| FR-10 | 4.4 | FR-25 | 7.1, 7.2 |
| FR-11 | 4.3, 4.6 | FR-26 | 7.1–7.3 |
| FR-12 | 4.2, 4.6 | FR-27 | 1.2, all |
| FR-13 | 4.5 | FR-28 | 1.1, 1.3 |
| FR-14 | 4.1, 7.1 | FR-29 | all test stories |
| FR-15 | 4.6 | | |

## Epic List

1. **Epic 1 (E0): Test Infrastructure & Scaffolding** — the gate exists before any production code
2. **Epic 2 (E1): Messaging Kernel** — the shared Inbox/Outbox machinery, highest test density
3. **Epic 3 (E2): ServiceDefaults** — options, locking, events, telemetry wiring
4. **Epic 4 (E3): CoreBankAPI** — the ledger: idempotent intake, atomic execution, event publishing
5. **Epic 5 (E4): PaymentsAPI** — intake, reliable forwarding, event consumption
6. **Epic 6 (E5): AppHost & Orchestration** — Aspire graph, config alignment, boot smoke
7. **Epic 7 (E6): Load Harness Realignment** — LoadTestSupport + k6 conform to the rebuilt system; presentation console
8. **Epic 8 (E7): Documentation Refresh** — ARCHITECTURE.md from code, ADRs for the rulings

---

## Epic 1: E0 — Test Infrastructure & Scaffolding

The coverage gate and rebuild solution filter exist and demonstrably enforce before any production code is rebuilt (FR-27, FR-28; AD-9, AD-10).

### Story 1.1: Test package versions and coverage gate

As the rebuild developer, I want the test stack pinned centrally and a coverage gate that plain `dotnet test` enforces, so that every later story inherits the same gate without per-project setup.

**Acceptance Criteria:**

**Given** `Directory.Packages.props`
**When** test packages are added
**Then** it pins xunit.v3 4.0.0, xunit.runner.visualstudio 4.0.0, Microsoft.NET.Test.Sdk 18.9.0, AwesomeAssertions 9.6.0, Moq 4.20.72, coverlet.collector + coverlet.msbuild 10.0.1, and the persistence-tier packages Testcontainers.PostgreSql 4.14.0 + Testcontainers.XunitV3 4.14.0 (ADR-016; the original EF Core SQLite pin was removed by story 6.6)
**And** `tests/Directory.Build.props` sets `CollectCoverage=true`, `Threshold=90`, `ThresholdType=line`, excludes `[*]*.Program`, AppHost assemblies, and `ExcludeByAttribute=ExcludeFromCodeCoverage`
**And** VSTest runner mode is in effect (no Microsoft.Testing.Platform opt-in anywhere).

### Story 1.2: Test projects and rebuild solution filter

As the rebuild developer, I want four scaffolded test projects and `CoreBankDemo.Rebuild.slnf`, so that the strangler gate has a home.

**Acceptance Criteria:**

**Given** the solution
**When** scaffolding completes
**Then** `tests/CoreBankDemo.{Messaging,ServiceDefaults,CoreBankAPI,PaymentsAPI}.Tests` exist, referencing their targets and the pinned packages, each with one passing smoke test
**And** `CoreBankDemo.Rebuild.slnf` contains ServiceDefaults, Messaging + the four test projects
**And** `dotnet test CoreBankDemo.Rebuild.slnf` passes. *(Note: Messaging and ServiceDefaults enter the filter here but their rebuild starts in their own epics; their old sources stay until then.)*

### Story 1.3: Gate proof

As the rebuild developer, I want proof the threshold actually fails builds, so that the gate is trusted for the whole rebuild.

**Acceptance Criteria:**

**Given** a temporary class with an uncovered branch in a filtered project
**When** `dotnet test CoreBankDemo.Rebuild.slnf` runs
**Then** the run fails on the coverage threshold
**And** after removing the canary the run passes — both outcomes captured in the story record.

---

## Epic 2: E1 — Messaging Kernel

Rebuild `CoreBankDemo.Messaging` as the single implementation of Inbox/Outbox machinery (FR-19, FR-20; AD-3, AD-4, AD-7, AD-8, AD-9, AD-11). Old Messaging sources are demolished at epic start; every class returns test-first.

### Story 2.1: Identity, constants, and message contracts

As a processor implementer, I want partition assignment, statuses, and message contracts defined once, so that every store agrees on identity and state names (FR-4, FR-19; AD-4).

**Acceptance Criteria:**

**Given** any idempotency key string
**When** `PartitionHelper.GetPartitionId(key, 4)` is called repeatedly anywhere
**Then** it returns the same FNV-1a-derived partition in [0,4) — property-tested for determinism, distribution, and known vectors
**And** `MessageConstants` defines Pending/Processing/Completed/Failed, MaxRetryCount=5, BatchSize=10, ProcessingTimeout=5min, PollingInterval defaults — no other status/limit literals exist in the kernel
**And** `IMessage`/`IInboxMessage`/`IOutboxMessage` expose id, idempotency/dedupe identity, PartitionId, Status, RetryCount, timestamps, TraceParent/TraceState, LastError.

### Story 2.2: Idempotent store

As a message producer, I want `StoreIfNewAsync` to be race-safe, so that duplicates never create second rows (FR-3, FR-19; AD-4).

**Acceptance Criteria:**

**Given** two concurrent stores with the same dedupe identity (persistence tier — real PostgreSQL per ADR-016; originally the SQLite-in-memory tier)
**When** both call `StoreIfNewAsync`
**Then** exactly one row exists; the loser reports "already exists" without throwing
**And** uniqueness violation detection goes through one helper keyed on Npgsql's SQLSTATE `23505` (ADR-016; originally SQLite + Postgres codes), never string matching at call sites
**And** command stores dedupe on key alone; event stores on composite identity (AD-4) — enforced by the repository base's unique-index definition hooks.

### Story 2.3: Claiming, retry, and poison state machine

As a processor, I want batch claiming and failure handling in the base repository, so that retry semantics are identical everywhere (FR-7; AD-3, AD-11).

**Acceptance Criteria:**

**Given** pending messages across partitions
**When** a batch is claimed for one partition
**Then** at most BatchSize ids return, oldest-first, only rows with RetryCount < MaxRetryCount, and claimed rows become Processing
**And** Processing rows older than ProcessingTimeout are reclaimed as claimable
**When** processing fails
**Then** the row returns to Pending with RetryCount+1 and LastError recorded; at MaxRetryCount it becomes terminal Failed
**And** `ExecuteInTransactionAsync` wraps multi-row updates atomically.

### Story 2.4: OutboxProcessorBase and delivery strategy port

As a service, I want the outbox poll/lock/dispatch loop implemented once with delivery pluggable, so that HTTP-forward and Dapr-publish are strategies, not loops (FR-5, FR-6, FR-8; AD-3, AD-8).

**Acceptance Criteria:**

**Given** a mocked `IDistributedLockService`, repository, and `IOutboxDeliveryStrategy`
**When** the processor ticks
**Then** it fans out over all 4 partitions in parallel, processes a partition only while holding `<prefix>-partition-<id>`, honors cancellation, and dispatches each message to the strategy inside a span restored from the stored TraceParent
**And** strategy success → Completed; strategy failure → the 2.3 retry path (never handled inside the strategy)
**And** no partition is processed concurrently by two ticks (lock contention test).

### Story 2.5: InboxProcessorBase and handler dispatch

As a consuming service, I want the same loop shape for inboxes with a handler port, so that consumption follows identical ordering/locking rules (FR-19; AD-3).

**Acceptance Criteria:**

**Given** mocked lock service, repository, and message handler
**When** the processor ticks
**Then** behavior mirrors 2.4 (partitions, locks, ordering, trace restoration, retry on handler failure)
**And** handler resolution happens per message in a fresh DI scope.

### Story 2.6: Kernel failure-path hardening

As the demo owner, I want the ugly paths proven, so that the kernel's guarantees are tests, not claims (FR-7, FR-29; AD-7, AD-11).

**Acceptance Criteria:**

**Given** lock acquisition failure, lock expiry mid-batch, strategy timeout, repository exception, and cancellation during dispatch
**When** each occurs
**Then** no message is lost or double-completed; work cancels cooperatively at the 5/6 lock-lifetime point; the tick survives and the next tick proceeds
**And** kernel line coverage ≥90%.

---

## Epic 3: E2 — ServiceDefaults

Rebuild options, locking, event types, and telemetry wiring (FR-20, FR-23; AD-6, AD-7). Demolition of old ServiceDefaults sources at epic start.

### Story 3.1: Validated processing options

As an operator, I want every processor option validated at startup, so that misconfiguration fails fast and dead options cannot exist (FR-23; AD-7, ruling A3/A4).

**Acceptance Criteria:**

**Given** options classes (`ProcessingOptionsBase` + Inbox/Outbox/MessagingOutbox variants with PubSub/Topic)
**When** binding runs with invalid values (PartitionCount ≠ 4, non-positive intervals, missing topic)
**Then** startup fails with all violations reported
**And** no `LockRenewIntervalSeconds` member exists; every member is read by kernel or wiring code (dead-option test via reflection against known consumers list)
**And** `Features:UseDapr` appears nowhere in code or config.

### Story 3.2: Distributed lock port and Dapr implementation

As the kernel, I want `IDistributedLockService` with a Dapr Redis implementation, so that partition exclusivity works in-process-mocked and live (FR-20; AD-6, AD-7).

**Acceptance Criteria:**

**Given** a mocked `DaprClient` (via its abstract surface or a thin adapter)
**When** a lock is acquired
**Then** the handle exposes a CancellationToken that fires at 5/6 of expiry; disposal unlocks; acquisition failure returns a non-throwing "not acquired" result
**And** a `NoOpDistributedLockService` exists for lock-free hosting (LoadTestSupport)
**And** logic (expiry fraction math, handle lifecycle) is unit-tested; only the Dapr call itself is adapter code.

### Story 3.3: CloudEvent types and publisher port

As CoreBankAPI, I want event records and an `IEventPublisher` port, so that event shapes live once and publishing is mockable (FR-15, FR-16; AD-6, AD-12).

**Acceptance Criteria:**

**Given** the frozen wire contracts
**When** the records are implemented
**Then** they match the AD-12 shapes byte-for-byte in JSON serialization (snapshot tests)
**And** `IEventPublisher.PublishAsync(type, source, subject, payload, traceParent)` maps to Dapr `PublishEventAsync` with CloudEvent metadata (`cloudevent.type/source/subject/traceparent`) in the adapter — metadata mapping unit-tested against a mocked publish call.

### Story 3.4: Service wiring defaults

As every service, I want `AddServiceDefaults` rebuilt thin, so that OTel, resilience, health, and lock registration are consistent (NFR-2, NFR-3).

**Acceptance Criteria:**

**Given** a test `WebApplicationBuilder`
**When** `AddServiceDefaults(serviceName, activitySources)` runs
**Then** OTel tracing/metrics/logging, OTLP export override via `JAEGER_OTLP_ENDPOINT`, `/health` + `/alive`, service discovery, and `AddStandardResilienceHandler` are registered (asserted via DI container inspection)
**And** hosting-only members carry `[ExcludeFromCodeCoverage]`; option-binding helpers are covered.

---

## Epic 4: E3 — CoreBankAPI

Rebuild the ledger service (FR-9..FR-16; AD-2, AD-4, AD-5, AD-11). Demolition at epic start; wire DTOs must match the frozen contracts.

### Story 4.1: Domain model, DbContext, and seeding

As the ledger, I want accounts and message stores defined with correct indexes, so that identity rules are enforced by the schema (FR-14; AD-4).

**Acceptance Criteria:**

**Given** `CoreBankDbContext`
**When** the model builds
**Then** `Accounts` (PK AccountNumber), `InboxMessages` (unique on TransactionId; partition/status/receivedAt index), `MessagingOutboxMessages` (unique on (TransactionId, EventType, AccountNumber); partition/status/createdAt index) exist — verified on real PostgreSQL (ADR-016; originally SQLite)
**And** startup seeding creates exactly the 3 demo accounts idempotently (second run adds nothing).

### Story 4.2: Transaction validation

As the ledger, I want pure validation logic, so that business rejection is deterministic and fully covered (FR-12).

**Acceptance Criteria:**

**Given** combinations of unknown/inactive accounts, insufficient funds, same-account transfer, invalid amounts
**When** `TransactionValidator.Validate` runs
**Then** each yields the specific failure reason; valid input yields success — table-driven tests, no mocks needed beyond account snapshots.

### Story 4.3: Account repository and transaction executor

As the ledger, I want money movement isolated and deterministic, so that balance arithmetic is provably correct (FR-11; AD-5, AD-9).

**Acceptance Criteria:**

**Given** an `IAccountRepository` port with a `FOR UPDATE` pass-through and a provider-agnostic load path used in tests
**When** `TransactionExecutor.Execute` runs with mocked accounts
**Then** accounts lock in alphabetical order, debit and credit apply exactly once, the cached `ResponsePayload` (frozen `TransactionResponse` shape) is produced, and validation failure produces a failure payload without touching balances
**And** executor logic reaches ≥90% coverage without Postgres, while the `FOR UPDATE` pass-through itself is proved on real PostgreSQL in the persistence tier (ADR-016; it is no longer coverage-excluded).

### Story 4.4: Idempotent transaction intake

As PaymentsAPI, I want `/api/transactions/process` to dedupe before logic, so that duplicates never execute twice (FR-9, FR-10; AD-4, AD-11).

**Acceptance Criteria:**

**Given** a valid `TransactionRequest`
**When** POSTed
**Then** the inbox row is stored via `StoreIfNewAsync` and `202` returns the frozen `TransactionResponse` with Pending status
**When** the same TransactionId is POSTed again
**Then** completed → cached ResponsePayload replayed verbatim; in-flight → `202` with current status (AD-11)
**And** `GET /api/transactions/{id}` reports status; validation errors return `BadRequest(new { Errors })` with all errors
**And** the controller contains no business logic (handler-tested; controller mapping-tested).

### Story 4.5: Account endpoints

As PaymentsAPI, I want validate/get endpoints, so that forwarding can pre-check destinations (FR-13).

**Acceptance Criteria:**

**Given** existing, inactive, and unknown accounts
**When** `POST /api/accounts/validate` / `GET /api/accounts/{number}` run
**Then** responses match the frozen `AccountValidationResponse`/`AccountDetailsResponse` shapes and semantics (unknown → IsValid=false / 404).

### Story 4.6: Atomic inbox execution with event enqueue

As the system, I want execution, completion, and events in one transaction, so that AD-5 holds (FR-11, FR-12, FR-15).

**Acceptance Criteria:**

**Given** the InboxProcessor built on `InboxProcessorBase` with an execution handler
**When** a pending transaction processes successfully
**Then** one DB transaction commits: balances changed, inbox row Completed with cached response, and exactly 3 outbox events enqueued (TransactionCompleted + 2× BalanceUpdated with correct deltas/new balances)
**When** validation rejects
**Then** the same single transaction commits: no balance change, inbox Completed with failure payload, TransactionFailed event enqueued — never status Failed (AD-11)
**When** the transaction throws mid-way
**Then** nothing commits and the kernel retry path takes over — proven with a failing-commit test.

### Story 4.7: Event publishing processor

As downstream consumers, I want enqueued events published as CloudEvents, so that the Dapr hop works on the kernel loop (FR-16; AD-3, AD-8).

**Acceptance Criteria:**

**Given** `MessagingOutboxProcessor` derived from `OutboxProcessorBase` with a Dapr-publish `IOutboxDeliveryStrategy`
**When** events process
**Then** each publishes via `IEventPublisher` with correct CloudEvent type constant, source, subject=TransactionId, and stored traceparent; publish failure follows the kernel retry path
**And** no polling/locking/retry code exists in this class beyond base-class configuration (asserted by review + no overrides of loop methods).

---

## Epic 5: E4 — PaymentsAPI

Rebuild the intake service (FR-1..FR-8, FR-17, FR-18). Demolition at epic start.

### Story 5.1: Payment store and idempotency-key handling

As a client, I want payments stored idempotently with my key, so that retries are safe (FR-2, FR-3, FR-4).

**Acceptance Criteria:**

**Given** `PaymentsDbContext` (OutboxMessages unique on IdempotencyKey; InboxMessages composite dedupe; partition/status indexes — verified on real PostgreSQL per ADR-016; originally SQLite-verified)
**When** a payment handler processes a request with/without `Idempotency-Key`
**Then** provided keys are used verbatim, absent keys become GUIDs, PartitionId = FNV-1a(key) % 4, and TraceParent/TraceState are captured on the row.

### Story 5.2: Payment intake endpoint

As a client, I want `POST /api/payments` to accept-and-acknowledge, so that my request survives downstream outages (FR-1, FR-2, FR-3).

**Acceptance Criteria:**

**Given** a valid `PaymentRequest`
**When** POSTed
**Then** `202` returns the frozen `PaymentResponse` (PaymentId, TransactionId=key, Pending) after `StoreIfNewAsync`
**When** a duplicate key arrives
**Then** `202` references the existing record without a second row
**And** invalid requests return all validation errors at once; the controller stays logic-free.

### Story 5.3: Contract-generated Kiota CoreBank client

As the forwarder, I want the CoreBank HTTP transport generated from its checked-in contract, so that every public operation stays contract-driven while application logic remains isolated from transport code (FR-8, FR-29; AD-6, AD-8, AD-9, AD-11, AD-12).

**Acceptance Criteria:**

**Given** CoreBankAPI's frozen HTTP surface
**When** its checked-in OpenAPI document is inspected
**Then** `CoreBankDemo.CoreBankAPI/OpenApi/corebank-api.json` describes all four public operations and their frozen request/response shapes: validate account, get account, process transaction, and get transaction status
**And** operation ids, status codes, content types, nullability, and success/error schemas are explicit
**And** changing a frozen HTTP shape remains ADR-gated.

**Given** PaymentsAPI is built
**When** Kiota generation runs
**Then** an incremental MSBuild target runs a repository-pinned Kiota version before compilation and generates the C# client from that checked-in document beneath `$(IntermediateOutputPath)`
**And** regeneration removes obsolete generated files before compiling the declared generated `Compile` items
**And** generated sources are excluded from version control and coverage, with a build leaving the working tree clean.

**Given** application forwarding code
**When** it calls CoreBankAPI
**Then** the generated client is wrapped by the single application-owned `ICoreBankApiClient` adapter; generated request/response types do not leak into handlers or delivery strategies
**And** the adapter resolves Aspire's logical `corebank-api` endpoint rather than any replica address
**And** the adapter propagates ambient `traceparent`/`tracestate`, maps generated models to application-owned domain results, and classifies every 2xx as delivery success and every non-2xx, timeout, or exception through the AD-11 retry outcome
**And** `Features:UseDapr`, the hand-written HTTP implementation, and every alternative CoreBank client are absent.

**Given** the rebuild test gate
**When** PaymentsAPI tests run
**Then** the generated client compiles from the checked-in document and adapter tests cover every operation, trace propagation, representative 2xx/4xx/5xx responses, malformed success bodies, cancellation, timeouts, and exceptions without requiring a live CoreBankAPI contract-diff job.

### Story 5.4: Forwarding processor

As the system, I want the outbox forwarded on the kernel loop, so that ordering and retry semantics hold (FR-5, FR-6, FR-7; AD-3, AD-11).

**Acceptance Criteria:**

**Given** `OutboxProcessor` on `OutboxProcessorBase` with an HTTP-forward strategy using `ICoreBankApiClient`
**When** a message processes
**Then** destination account is validated, the transaction submitted, 2xx (incl. duplicate-accept) → Completed, anything else → kernel retry path, Failed after MaxRetryCount
**And** per-partition ordering under concurrent partitions is proven with an interleaving test.

### Story 5.5: Event subscription intake

As PaymentsAPI, I want Dapr events stored idempotently, so that duplicate deliveries are harmless (FR-17; AD-4).

**Acceptance Criteria:**

**Given** the four subscription endpoints (completed/failed/balance-updated/unknown)
**When** CloudEvents arrive (including duplicates and unknown types)
**Then** each stores an inbox row with composite dedupe identity `TransactionId-EventType[-AccountNumber]`, duplicates are dropped with a log (200, not error), unknown types land in the unknown handler
**And** the declarative subscription YAML routes by event type exactly as on `main`.

### Story 5.6: Event handling processor

As the demo, I want consumed events processed on the kernel loop, so that the full round-trip is visible in traces (FR-18; AD-3, AD-8).

**Acceptance Criteria:**

**Given** `InboxProcessor` on `InboxProcessorBase` with `TransactionEventHandler`
**When** events process
**Then** dispatch by EventType deserializes the frozen event records, logs structured entries, tags the restored span — and mutates no local state
**And** PaymentsAPI logic coverage ≥90%.

---

## Epic 6: E5 — AppHost & Orchestration

Rebuild and replicate the Aspire graph, and align persistence tests with the production PostgreSQL engine; the full `.sln` returns to green here (FR-6, FR-21, FR-22, FR-23, FR-24; AD-3, AD-9, AD-10, AD-13).

### Story 6.1: Aspire application graph

As the demo owner, I want one-command startup, so that the talk demo boots reliably (FR-21, FR-23, NFR-5).

**Acceptance Criteria:**

**Given** `aspire run` (per `aspire-launch` skill)
**When** the AppHost starts
**Then** Postgres (paymentsdb, corebankdb, pgAdmin), Redis (+ RedisInsight), Jaeger, Dapr pub/sub and subscription components, and both APIs with sidecars come up healthy; both APIs receive the shared Aspire Redis connection for distributed locking, and no Dapr `lockstore` component exists
**And** every service config has PartitionCount=4 and no dead flags; `CoreBankDemo.Rebuild.slnf` now equals the full solution's buildable set and `dotnet build CoreBankDemo.sln` is green.

### Story 6.2: Renewable Redis distributed locking

As the demo owner, I want partition locks renewed through the Aspire-managed Redis instance, so that healthy work can safely outlive its initial lease while the demo remains one-command local (FR-20, FR-21, FR-23; AD-6, ADR-011).

**Acceptance Criteria:**

**Given** the existing `IDistributedLockService` consumers
**When** the Dapr lock adapter is replaced with `DistributedLock.Redis`
**Then** the public interface and Messaging call sites remain unchanged, lock acquisition is non-blocking, held locks renew automatically, and caller cancellation or `HandleLostToken` cancels the workload
**And** contention, cancellation, Redis failures, workload failures, and release failures preserve the current non-throwing `false` result contract.

**Given** the regular Aspire AppHost
**When** it starts locally
**Then** both APIs receive the shared `redis` connection and wait for Redis, Dapr remains available for pub/sub, and the Dapr `lockstore` component is absent
**And** a real-Redis integration proof holds a lock beyond its initial expiry and prevents a second contender until release.

**Given** AD-7 and ADR-004 previously specified non-renewed Dapr locking
**When** this story starts implementation
**Then** ADR-011 is the accepted superseding decision; frozen completed Story 3.2 remains historical rather than being rewritten.

### Story 6.3: Replicated local API topology

As the demo owner, I want competing local API instances by default, so that the demo proves partition ordering and exclusivity hold beyond a single process (FR-6, FR-21, FR-23, FR-24; NFR-1, NFR-5; AD-3, AD-9, AD-13).

**Acceptance Criteria:**

**Given** either the regular AppHost or the LoadTests AppHost
**When** its default topology starts
**Then** it runs two PaymentsAPI replicas and two CoreBankAPI replicas, each with a healthy Dapr sidecar connected to the shared pubsub and each application connected to the shared Aspire Redis lock store
**And** replicas of each service share its database and logical Dapr app id while sidecar/runtime ports remain unique
**And** both replicas start reliably against an empty database without racing schema initialization
**And** the four-partition configuration and existing external HTTP shapes remain unchanged.

**Given** demo clients or k6 need PaymentsAPI
**When** they resolve the service
**Then** they use one stable Aspire-proxied PaymentsAPI endpoint preserving the documented entry port (5294 regular, 5295 load test) rather than a replica address or a new gateway
**And** PaymentsAPI resolves CoreBankAPI through Aspire's logical service endpoint.

**Given** two service instances compete for work
**When** messages from the same partition are processed
**Then** the Postgres acceptance tier with the real renewable Redis lock adapter proves at most one instance owns that store partition at a time and messages complete in durable enqueue order without reordering, including equal ordering timestamps
**And** processor-instance evidence proves both replicas perform work while different partitions progress concurrently
**And** lock-expiry takeover is not duplicated here because Story 2.6 owns that failure path.

**Given** the LoadTests AppHost is preparing a run
**When** reset executes
**Then** both APIs first complete their existing schema initialization while every hosted processor remains behind a load-test-only start gate
**And** an explicit one-shot initializer runs after API and LoadTestSupport health, resets the databases, releases every processor gate, and completes before k6 starts
**And** focused tests prove no processor tick occurs before release; k6 verifies the clean state but is not responsible for startup ordering.

### Story 6.4: Chaos opt-in and demo smoke

As the speaker, I want DevProxy and the demo flows verified, so that talk stages 0–4 work (FR-22, NFR-5).

**Acceptance Criteria:**

**Given** the running AppHost
**When** `demo-requests.http` and `payment-idempotency-tests.http` flows run
**Then** all behave as on `main` (202s, duplicate replay, outbox/inbox visibility via LoadTestSupport endpoints once E6 lands — until then via DB)
**And** enabling DevProxy injects faults and the Polly layer retries visibly in Jaeger; one payment renders as one trace (NFR-2).

### Story 6.5: OpenTelemetry business metrics

As the demo operator, I want low-cardinality business and messaging metrics exported through OpenTelemetry, so that I can quantify payment intake, transaction outcomes, message movement, and Inbox/Outbox health without reconstructing them from logs.

**Acceptance Criteria:**

**Given** either API starts through `AddServiceDefaults`
**When** OpenTelemetry metrics are configured
**Then** the shared business `Meter` is registered and exported through the existing OTLP pipeline, with instrument names, units, descriptions, and allowed tag values defined once
**And** metric tags use only bounded values (store name/kind, transport, message type, and outcome); transaction ids, idempotency keys, account numbers, trace ids, exception text, and other user-controlled values never become metric attributes.

**Given** payment and transaction intake
**When** an intake reaches a known outcome
**Then** counters record payment `stored`/`duplicate`/`validation_failed` and transaction `accepted`/`replayed`/`in_flight`/`transport_failed` outcomes exactly once
**And** committed transaction execution records `completed` or `business_rejected` exactly once; a business rejection is never counted as an Inbox or transport failure.

**Given** an Inbox or Outbox store operation
**When** persistence succeeds, loses a dedupe race, or fails
**Then** a counter records `added`, `duplicate`, or `failed` for the stable store name and store kind
**And** failed persistence is recorded before the original exception propagates unchanged.

**Given** the shared Inbox/Outbox processors
**When** a claimed item completes, schedules a retry, reaches terminal `Failed`, or completion/retry persistence fails
**Then** item-processing counters record the authoritative outcome exactly once and queue-duration histograms record non-negative milliseconds from durable enqueue/receive time to processing start
**And** cancellation and lock contention are not misclassified as processing failures.

**Given** Payments→CoreBank HTTP delivery or CoreBank→Payments Dapr delivery
**When** a send/receive attempt succeeds, fails, is deduplicated, or is routed as unknown
**Then** transport counters record direction, bounded transport, message type, and outcome at the concrete transport boundary
**And** retries remain visible as attempts without claiming exactly-once physical delivery.

**Given** focused tests using `MeterListener`
**When** every outcome path is exercised
**Then** each expected measurement and tag set is asserted, forbidden high-cardinality tags are absent, failures still propagate, and the full rebuild test/coverage gate remains green.

### Story 6.6: Remove SQLite with PostgreSQL Testcontainers

As a maintainer, I want persistence integration tests to run against disposable PostgreSQL containers, so that tests prove the SQLSTATE, locking, transaction, and data-type behavior used in production without slowing the Docker-free unit-test loop.

**Acceptance Criteria:**

**Given** the current test suite and ADR-012/AD-9 SQLite test-tier decision
**When** this story starts implementation
**Then** ADR-016 is accepted to supersede only the SQLite-specific parts of ADR-012 and AD-9 while preserving the fast-unit, persistence-integration, and distributed-acceptance tiers
**And** the PostgreSQL container image is explicitly pinned to the production/AppHost major version rather than using `latest`.

**Given** tests that exercise EF Core models, repositories, durable Inbox/Outbox stores, seeding, transactions, or provider behavior
**When** the persistence integration target runs
**Then** they use real PostgreSQL through Testcontainers, with one amortized container lifecycle and isolated databases/schemas for parallel tests
**And** real Npgsql behavior is proven for SQLSTATE `23505`, `SELECT ... FOR UPDATE`, rollback/atomicity, concurrent deduplication, durable ordering, and relevant `decimal`/`DateTimeOffset` round trips.

**Given** pure domain, handler, processor, option, telemetry, and adapter-orchestration logic
**When** the unit-test target runs
**Then** it remains Docker-free, deterministic, and independently runnable
**And** persistence adapters are not faked with a second relational provider.

**Given** the repository's test entry points
**When** maintainers select unit, persistence-integration, or full rebuild validation
**Then** named solution filters/commands run those scopes independently, the integration target fails clearly when no container runtime is available, and the full gate runs both tiers
**And** combined coverage remains at least 90% without blanket persistence exclusions or a weaker threshold disguised by moving tests.

**Given** the migration is complete
**When** active source, project, package, test-support, and forward-looking planning files are inspected
**Then** no SQLite provider package, `UseSqlite`, SQLite fixture, or SQLite-specific exception branch remains
**And** frozen completed stories remain an unmodified historical record.

### Story 6.7: Eliminate Dapr service invocation

As a maintainer, I want every production API-to-API request/response call to use the contract-generated Kiota client, so that the repository contains no deprecated Dapr service-invocation path, switch, or fallback while retaining Dapr only for CloudEvent pub/sub.

**Acceptance Criteria:**

**Given** completed Story 5.3 and accepted ADR-008/ADR-013
**When** the current implementation is inspected
**Then** the existing `ICoreBankApiClient`/`KiotaCoreBankApiClient` path is recognized as the sole PaymentsAPI→CoreBankAPI implementation rather than rebuilt or duplicated
**And** no new ADR is required because this story completes and enforces the already-accepted decision.

**Given** production source, configuration, AppHosts, project files, tests, scripts, and active documentation
**When** the cleanup is complete
**Then** `Features:UseDapr`, `Features__UseDapr`, Dapr invocation clients/handlers, sidecar invocation URLs/headers, and deprecated invocation API calls are absent
**And** an automated architecture guard rejects their reintroduction.

**Given** PaymentsAPI forwards a payment to CoreBankAPI
**When** it validates the destination account and submits the transaction
**Then** both operations flow through the checked-in OpenAPI contract, generated Kiota client, application-owned adapter, Aspire logical `corebank-api` endpoint, standard service discovery/resilience pipeline, and existing trace propagation
**And** there is no parallel hand-written HTTP client or alternate transport fallback.

**Given** CoreBankAPI publishes events and PaymentsAPI receives them
**When** the Dapr integration is inspected and its regression tests run
**Then** Dapr remains only for pub/sub sidecars, subscription delivery, and `PublishEventAsync`
**And** this story does not replace CloudEvent pub/sub with Kiota or remove a Dapr package still required by that event path.

**Given** frozen completed stories and accepted decision records describe the superseded route historically
**When** repository-wide verification runs
**Then** historical context may retain explicit past-tense references, while executable code, live config, current guidance, generated inputs, and forward-looking backlog artifacts describe only the Kiota request/response path.

---

## Epic 7: E6 — Load Harness Realignment

LoadTestSupport and k6 conform to the rebuilt system, then expose their proven workflow through a presentation-safe local console (FR-24, FR-25, FR-26; NFR-5; harness adapts to code).

### Story 7.1: Assertion API realignment

As the acceptance tier, I want reset/drain/assert working against the new schemas, so that the five invariants are machine-checked (FR-25, FR-26).

**Acceptance Criteria:**

**Given** the rebuilt DbContexts
**When** `/reset`, `/assert/drain`, `/assert/results?expectedUnique=N`, and the inbox/outbox inspection endpoints run
**Then** semantics match `main`: truncate stores + reset the 10 LOAD accounts (sole owner of that dataset); drain = zero non-terminal; results assert exactly-once, all-submitted-processed, balance conservation, zero failed, balances-correct-by-replay
**And** pure assertion logic is covered by Docker-free unit tests while persistence queries are integration-tested against seeded PostgreSQL Testcontainers data from Story 6.6.

### Story 7.2: MCP server tools

As the agent harness, I want the MCP tools back, so that `/run-load-tests` automation works (FR-25).

**Acceptance Criteria:**

**Given** the MCP server at port 5181 root
**When** `reset_database`, `poll_until_drained`, `get_assertion_results`, `get_*_inbox/outbox` are invoked
**Then** they wrap the 7.1 endpoints with identical semantics and structured outputs.

### Story 7.3: k6 run and first full acceptance gate

As the demo owner, I want the end-to-end load test green, so that the rebuild is proven equivalent (FR-24, FR-26, NFR-1).

**Acceptance Criteria:**

**Given** the LoadTests AppHost (disposable infra, k6 container, 10% duplicate ratio)
**When** a full run executes (reset → k6 → drain → assertions)
**Then** all five invariants pass; failures are triaged as code-bug vs harness-mismatch (harness adapts unless an invariant is genuinely violated)
**And** a trace analysis (`corebank-trace-analysis` skill) shows intact traces across both hops.

### Story 7.4: Presentation-safe terminal demo console

As the speaker, I want a mouse-enabled terminal control room for my talks, so that I can preflight, rehearse, and run each live cue from one dependable place without juggling request files, terminals, configuration, and dashboards (NFR-5; human scope amendment 2026-08-29).

**Acceptance Criteria:**

**Given** the standalone `CoreBankDemo.DemoRunner` console project
**When** it starts locally
**Then** it validates the selected talk scenario and prerequisites before launching or explicitly attaching to the required Aspire topology
**And** it presents a three-pane terminal UI (talk cues, current cue, system confidence) with complete mouse and keyboard operation, responsive layout, and safe Run, Retry, Details, open-dashboard, and Stop actions.

**Given** the checked-in `MissionCriticalTalk-v7` scenario derived from the author's 55-slide deck
**When** Show or Rehearsal mode reaches the live cues
**Then** the runner pre-arms and gates “Inbox at work” (slide 42), the Aspire/k6 resilience proof (slides 45–52), and the development-environment hand-off (slide 53)
**And** the load proof makes the deck's Run → Wait → Assert → Investigate phases and their evidence visible without inventing a second acceptance workflow.

**Given** any action is running, failed, cancelled, or ambiguous
**When** the speaker tries to advance
**Then** Next remains unavailable, duplicate activation is suppressed, and the current cue offers concise Retry, Details, or restart-from-checkpoint choices
**And** the runner never embeds banking logic, connects directly to stores, mutates checked-in configuration, executes scenario-supplied shell commands, fakes live success, or stops an unowned process.

**Given** Stories 7.1–7.3 are not yet done
**When** Story 7.4 implementation proceeds
**Then** the scenario model, state machine, process ownership, allow-listed adapters, TUI, and load-workflow presentation contract may be implemented and tested through ports and fakes
**And** live LoadTestSupport binding, a successful five-invariant rehearsal proof pack, and Story 7.4 completion remain blocked until Stories 7.1–7.3 are done
**When** Story 7.3 establishes the accepted load workflow
**Then** Story 7.4 binds to those exact endpoints and evidence semantics, completes the live rehearsal and presentation-terminal dress rehearsal, and introduces no parallel assertion path.

---

## Epic 8: E7 — Documentation Refresh

Docs describe only what exists (ruling A5; NFR-3, NFR-4).

### Story 8.1: Regenerate ARCHITECTURE.md

As a repo visitor, I want the architecture doc generated from the rebuilt code, so that no phantom components remain.

**Acceptance Criteria:**

**Given** the rebuilt solution
**When** ARCHITECTURE.md is regenerated
**Then** every referenced class/endpoint/table exists in code; schemas match the DbContexts; config values match appsettings; the doc links the ADR set.

### Story 8.2: ADRs and skill updates

As the process record, I want the accepted rebuild decisions audited against the final code and documentation, so that ADR-008..ADR-016, their supersession links, and the project skills describe the implemented system accurately (rulings A1–A4 and A7–A8; NFR-4).

**Acceptance Criteria:**

**Given** ADR-008..ADR-016 are accepted
**When** the final documentation audit runs
**Then** each ADR's status, Context, Decision, Consequences, supersession links, and implementation references match the final code; ADR-012 identifies ADR-016's PostgreSQL Testcontainers supersession, ADR-015 records the presentation-tool exception, and no forward-looking artifact refers to an undefined ruling
**And** `.claude/skills` (`conventions`, `messaging-patterns`, `observability`) are updated where implemented surfaces changed; `ARCHITECTURE.md` links the complete ADR set; and the `AGENTS.md` rebuild section flips to "completed" only after the implementation and acceptance gates pass.
