# Epic 5 Context: E4 — PaymentsAPI

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Rebuild PaymentsAPI as the durable entry point for payment requests. The service must accept and deduplicate submissions, reliably forward accepted work to CoreBankAPI, and consume the resulting domain events while preserving the external behavior of the existing demo. The completed epic closes the end-to-end payment loop without weakening safe retries, per-key ordering, message durability, or distributed tracing.

## Stories

- Story 5.1: Payment store and idempotency-key handling
- Story 5.2: Payment intake endpoint
- Story 5.3: Contract-generated Kiota CoreBank client
- Story 5.4: Forwarding processor
- Story 5.5: Event subscription intake
- Story 5.6: Event handling processor

## Requirements & Constraints

Payment intake must retain the frozen `POST /api/payments` contract. A valid request is stored durably before PaymentsAPI returns `202 Accepted` with pending status. The supplied `Idempotency-Key` is authoritative; if none is supplied, the service generates a GUID-formatted key. Repeating a key returns the existing payment rather than creating another outbox row. Validation reports all errors together, and controllers contain no business logic.

Accepted payments are forwarded to CoreBankAPI over HTTP by validating the destination account and then submitting the transaction. Processing is oldest-first within a partition, different partitions may progress concurrently, and competing replicas must never process the same store partition simultaneously. Failed transport attempts return to pending with recorded error and incremented retry count; retry exhaustion is the only reason forwarding becomes terminally failed. Every 2xx response, including duplicate acceptance, completes delivery.

PaymentsAPI consumes transaction-completed, transaction-failed, and balance-updated CloudEvents from Dapr pubsub `pubsub` on topic `transaction-events`. Events are stored idempotently so duplicate broker deliveries return success without adding another inbox record. The existing completed, failed, balance-updated, and unknown event routes remain stable. Event handling is observational only: structured logging and span tagging, with no mutation of local business state.

The rebuild must preserve exactly-once business processing, zero loss of accepted payments, terminal-state completeness, and per-key ordering under retries and concurrent replicas. Trace context must survive the intake store, HTTP forwarding, event subscription, and inbox processing so one payment remains one distributed trace. Frozen HTTP DTO shapes, event records, endpoint behavior, and messaging names cannot change without an ADR.

Implementation is test-first and must keep `CoreBankDemo.Rebuild.slnf` green. PaymentsAPI logic is covered by the repository’s enforced minimum line coverage. Generated transport sources and hosting wiring may be excluded, but validation, handlers, repositories, idempotency behavior, delivery classification, adapter mapping, trace propagation, and event dispatch remain testable. Use xUnit, AwesomeAssertions, and Moq; repository behavior is verified with EF Core SQLite in-memory, while replicated Postgres and Redis semantics remain acceptance-tier concerns.

## Technical Decisions

Both PaymentsAPI processors reuse the shared messaging kernel. PaymentsAPI must not duplicate polling, partition fan-out, distributed locking, batch claiming, stale-claim recovery, retry and poison handling, or trace restoration. HTTP forwarding is supplied through the outbox delivery-strategy port, and event processing uses the inbox handler seam. Partition assignment uses the shared FNV-1a algorithm with a validated partition count of four and store-specific partition lock names.

Persistence uses EF Core and `Database.EnsureCreated()` rather than migrations. Idempotency is enforced by unique indexes plus race-safe insert handling, never check-then-insert. The payment outbox deduplicates on the idempotency key. The event inbox uses a composite identity based on transaction, event type, and account where applicable, because one transaction legitimately produces multiple events. Message records persist partition, transport status, retry data, timestamps, and trace context.

CoreBankAPI integration has one application-owned boundary: `ICoreBankApiClient`. Its transport implementation wraps a Kiota client generated during the build from CoreBankAPI’s checked-in OpenAPI document. Generated files live under the intermediate output path, are not committed, are excluded from coverage, and cannot leak generated models into application handlers or delivery strategies. The adapter resolves Aspire’s logical `corebank-api` endpoint, propagates `traceparent` and `tracestate`, maps transport data to application-owned results, and applies the shared delivery-outcome rules. Alternative hand-written clients, Dapr service invocation, and the obsolete `Features:UseDapr` flag are excluded.

The service follows hexagonal boundaries: HTTP and Dapr endpoints are inbound adapters; EF, Kiota HTTP, and messaging integrations are outbound adapters; application logic depends on ports, `TimeProvider`, and `ILogger<T>`. Message statuses represent transport state rather than business outcome. Configuration is validated at startup, package versions are centrally managed, constants own status and CloudEvent names, and structured logs include idempotency and partition context where applicable.

## Dependencies

This epic relies on the rebuilt messaging kernel and ServiceDefaults for race-safe stores, processor bases, partition locking, validated processing options, trace restoration, and shared CloudEvent contracts. It also relies on CoreBankAPI’s stable account and transaction endpoints, duplicate-response semantics, checked-in OpenAPI description, and published event shapes.

At epic start, the old PaymentsAPI implementation is demolished and the rebuilt project enters the rebuild solution filter. Later orchestration work supplies replicated instances behind stable Aspire ingress, logical CoreBankAPI service discovery, shared persistence and lock infrastructure, and healthy Dapr sidecars. The load harness will validate the completed flow against the fixed system invariants.
