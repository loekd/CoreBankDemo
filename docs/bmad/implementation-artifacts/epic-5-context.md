# Epic 5 Context: PaymentsAPI

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Rebuild PaymentsAPI as the durable entry point for payment requests: accept and deduplicate client submissions, reliably forward them to CoreBankAPI, and consume resulting domain events without changing the demo’s external behavior. This epic matters because it closes the end-to-end payment loop while preserving safe retries, per-key ordering, zero message loss, and one distributed trace across HTTP, message stores, and Dapr pub/sub.

## Stories

- Story 5.1: Payment store and idempotency-key handling
- Story 5.2: Payment intake endpoint
- Story 5.3: Contract-generated Kiota CoreBank client
- Story 5.4: Forwarding processor
- Story 5.5: Event subscription intake
- Story 5.6: Event handling processor

## Requirements & Constraints

Payment intake must preserve the frozen API contract. Valid requests are durably stored before returning `202 Accepted` with pending status. The client’s `Idempotency-Key` is used verbatim; when absent, a GUID-formatted key is generated. Duplicate keys must reference the existing payment and never create another outbox row. Request validation must report all errors together, and controllers must remain free of business logic.

Accepted work must eventually be forwarded to CoreBankAPI by first validating the destination account and then submitting the transaction. Forwarding is ordered oldest-first within each partition, partitions may progress concurrently, and a shared distributed lock prevents competing service instances from processing the same store partition simultaneously. Any non-2xx response, timeout, or exception follows the common retry path; exhausted retries become terminal transport failures. Every 2xx response, including duplicate acceptance, completes delivery.

PaymentsAPI must consume transaction-completed, transaction-failed, and balance-updated CloudEvents from Dapr topic `transaction-events`. Deliveries are stored idempotently using event-specific composite identity, so duplicate broker deliveries succeed without creating a second inbox record. Unknown event types must follow the existing unknown-event route. Event handling remains observational only: structured logging and span tagging, with no local business-state mutation.

The epic must preserve the system-wide invariants under retries and concurrent replicas: exactly-once business processing, no accepted-message loss, terminal completeness, and per-key ordering. Stored and forwarded messages must retain trace context so one payment remains one distributed trace. The frozen request, response, event, pubsub, topic, and endpoint semantics may not change without an ADR.

All logic must be developed test-first and remain within the rebuild gate. PaymentsAPI logic is subject to the existing line-coverage threshold; generated transport code and hosting boilerplate are excluded appropriately, while application-owned handlers, adapters, delivery classification, validation, idempotency, and event dispatch remain testable.

## Technical Decisions

PaymentsAPI uses the shared messaging kernel for both outbox forwarding and inbox event handling. It must not implement custom polling, partition fan-out, locking, claiming, stale-claim recovery, retry, poison handling, or trace restoration. HTTP forwarding is an `IOutboxDeliveryStrategy`; event handling is dispatched through the kernel inbox handler seam. Partition assignment is consistently derived from the idempotency key using FNV-1a with a validated partition count of four.

Persistence uses EF Core with `EnsureCreated()` and schema-enforced uniqueness rather than check-then-insert. The payment outbox dedupes on the idempotency key alone. The event inbox dedupes on transaction, event type, and account identity where applicable, because one transaction can legitimately produce multiple events. Message rows carry partition, transport status, retry information, timestamps, and persisted trace context.

CoreBankAPI integration has one application-owned port, `ICoreBankApiClient`, backed by a Kiota client generated during build from CoreBankAPI’s checked-in OpenAPI document. Generated sources live under the intermediate output path, are not committed, and never leak generated models into handlers or delivery strategies. The adapter resolves Aspire’s logical `corebank-api` endpoint, propagates `traceparent` and `tracestate`, maps transport models into application-owned results, and applies the common delivery-outcome contract. Hand-written or alternative CoreBank clients and the `Features:UseDapr` flag are not permitted.

The service follows the established hexagonal boundaries: thin ASP.NET and Dapr endpoints inbound; EF, Kiota HTTP, and messaging infrastructure outbound; application logic dependent only on ports, `TimeProvider`, and structured logging. Message statuses describe transport state only. Configuration is validated at startup, clocks are injected, package versions remain centrally managed, and logs include idempotency and partition context where relevant.

## Cross-Story Dependencies

This epic depends on the completed messaging-kernel and ServiceDefaults capabilities for idempotent stores, processor bases, distributed locks, processing options, trace restoration, and shared event contracts. It also depends on CoreBankAPI’s frozen account and transaction operations, checked-in OpenAPI document, duplicate semantics, and published CloudEvent shapes.

Within the epic, the payment store and identity rules underpin intake and forwarding. The generated-client adapter must exist before the forwarding strategy can be completed. Event subscription storage must exist before the inbox processor can dispatch consumed events.

Later orchestration work must provide replicated PaymentsAPI instances behind a stable Aspire ingress, logical CoreBankAPI service discovery, shared databases and lock stores, and healthy Dapr sidecars. The load-harness epic will adapt its assertions to these rebuilt stores while retaining the fixed end-to-end invariants.
