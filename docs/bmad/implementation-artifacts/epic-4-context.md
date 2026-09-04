# Epic 4 Context: CoreBankAPI

<!-- Generated from planning artifacts. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Rebuild CoreBankAPI as the ledger service that accepts idempotent transaction requests, executes money movement exactly once, exposes account-validation and account-details endpoints, and publishes transaction outcome events without changing the demo’s external behavior. This epic matters because it is where the system’s core guarantees are enforced: balance conservation, per-key ordering, replay-safe processing, and transactional coupling between ledger state and emitted events.

## Stories

- Story 4.1: Domain model, DbContext, and seeding
- Story 4.2: Transaction validation
- Story 4.3: Account repository and transaction executor
- Story 4.4: Idempotent transaction intake
- Story 4.5: Account endpoints
- Story 4.6: Atomic inbox execution with event enqueue
- Story 4.7: Event publishing processor

## Requirements & Constraints

CoreBankAPI must keep the frozen external contract intact: transaction intake still accepts a transaction request and returns `202` on accepted work, duplicate requests never re-run business logic, completed duplicates replay the cached response, and in-flight duplicates report current status. The status endpoint must report transaction state by idempotency key, while account endpoints must support destination validation and account-detail lookup with unchanged response semantics.

Transaction processing must be exactly-once at the business level. Each request is deduped by transaction id, which is also the idempotency key in this service. Validation failures are terminal business outcomes, not transport failures: they must cache a failure response, avoid balance changes, enqueue a failure event, and complete processing without retrying. Success must update balances once, cache the success response, and enqueue the related domain events.

Execution must preserve the system invariants that drive the overall demo: exactly-once processing, zero message loss, balance conservation, terminal completeness, and per-key ordering. Trace context must survive intake, processing, and event publication. Request validation must return all detected input errors together. The epic also inherits rebuild constraints: no new external behavior, no EF migrations, and all work should stay green through the rebuild solution filter with the existing coverage expectations for logic-heavy code.

Startup data ownership is fixed. CoreBankAPI is responsible only for idempotently seeding the three demo accounts; the load-test account set belongs to LoadTestSupport and must not be duplicated here.

## Technical Decisions

CoreBankAPI is built on the shared messaging kernel and ServiceDefaults abstractions instead of custom polling or transport code. Idempotent stores must rely on schema-enforced uniqueness plus shared store helpers rather than check-then-insert logic. Partitioning and ordering use a single rule everywhere: `PartitionId` is derived from the idempotency key with FNV-1a hashing and a partition count of four.

The ledger’s critical write path is atomic by design. Ledger mutation, inbox completion with cached response, and domain-event enqueue must commit in one database transaction, and no network I/O is allowed inside that transaction. Event publication happens only after commit through a messaging outbox processor using the same partition, locking, retry, and trace-restoration patterns as the other processors in the system.

Infrastructure access stays behind explicit ports and thin repositories. Distributed locking, event publishing, time, and persistence dependencies should be injected through the established interfaces, while provider-specific SQL is confined to minimal repository pass-throughs. Repositories are otherwise expected to stay provider-agnostic so logic can be proven in unit tests and store behavior can be exercised on SQLite.

Message status values are transport states, not business outcomes. Business rejection still results in a completed inbox item with a cached failure response and a `TransactionFailed` event. Event identities are also deliberate: the transaction intake store dedupes on the idempotency key alone, while the messaging outbox dedupes per emitted event so one transaction can legitimately produce multiple records. Published events must use the fixed CloudEvent type constants, Dapr pubsub/topic, and propagated trace context already defined for the demo.

Service conventions in this epic remain strict: controllers stay thin and avoid business logic, request validation surfaces aggregated errors, `TimeProvider` is used instead of direct clock access, structured logging includes idempotency and partition context where applicable, persistence uses EF Core with `EnsureCreated()`, and rebuild validation runs through `CoreBankDemo.Rebuild.slnf`.

## Cross-Story Dependencies

This epic depends on the messaging kernel and ServiceDefaults epics being in place first, because CoreBankAPI reuses their message contracts, processors, locking, event publisher port, CloudEvent constants, and trace-handling rules. Within the epic, the data model and store shape underpin every later story; validation and execution behavior then feed both intake semantics and atomic inbox processing; and event publication only makes sense after transactional event enqueue exists.

CoreBankAPI also provides capabilities used outside the epic: PaymentsAPI depends on the account-validation endpoint for forwarding checks, on transaction intake for reliable handoff, and later on the published transaction events for downstream status handling. That makes API compatibility and event semantics a cross-epic contract, not an implementation detail.
