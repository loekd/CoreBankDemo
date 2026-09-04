# Epic 5 Context: E4 — PaymentsAPI

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Rebuild PaymentsAPI as the durable intake and client-facing status edge of the payment flow. The service must accept a payment before downstream processing is available, preserve retries through an idempotent outbox, forward accepted work to CoreBankAPI without losing ordering or trace continuity, and consume resulting transaction events idempotently. The rebuilt service must preserve the frozen external behavior while expressing all polling, retry, locking, and trace-restoration behavior through the shared messaging kernel.

## Stories

- **5.1 — Payment store and idempotency-key handling**
- **5.2 — Payment intake endpoint**
- **5.3 — Contract-generated Kiota CoreBank client**
- **5.4 — Forwarding processor**
- **5.5 — Event subscription intake**
- **5.6 — Event handling processor**

## Requirements & Constraints

- `POST /api/payments` validates account identifiers, amount, and currency and returns all validation failures together. Controllers remain thin: bind the request, invoke application logic, and map the result.
- Acceptance is durable and asynchronous. A valid payment is stored in the Payments Outbox before returning `202 Accepted` with the frozen payment response shape and `Pending` status.
- The client-provided `Idempotency-Key` is used verbatim; when absent, a GUID-formatted key is generated. A repeated key returns the existing accepted payment and must never create a second outbox row.
- The payment key is also the transaction identity and ordering identity. Partition assignment uses FNV-1a over the key with a fixed partition count of four. Every stored message captures `TraceParent` and `TraceState`.
- Forwarding validates the destination account and submits the transaction to CoreBankAPI over HTTP. Any 2xx response, including duplicate acceptance, completes delivery. A 4xx, 5xx, timeout, cancellation caused by transport failure, malformed success response, or exception follows the messaging retry path and becomes terminal `Failed` only after the configured retry limit.
- Processing is oldest-first within each partition. A shared distributed lock prevents two service replicas from processing the same store partition concurrently, while separate partitions remain eligible for parallel progress. Stale processing claims must be reclaimable.
- PaymentsAPI subscribes to the transaction event topic through declarative Dapr routing. Completed, failed, balance-updated, and unknown event types are accepted through their designated routes.
- Event intake is idempotent. The inbox dedupe identity is composite because one transaction can legitimately produce multiple events: transaction id, event type, and account number where applicable. Duplicate deliveries return success and are logged rather than retried.
- Event handling restores trace context, dispatches by event type, deserializes the frozen event records, emits structured logs, and tags the active span. It does not mutate PaymentsAPI state.
- The service must retain the frozen HTTP DTOs, event payloads, CloudEvent types, topic names, and subscription behavior. Contract changes require an explicit architecture decision rather than incidental implementation drift.
- Tests are written first with xUnit, AwesomeAssertions, and Moq. PaymentsAPI logic must achieve at least 90% line coverage. PostgreSQL persistence behavior is tested against the pinned PostgreSQL Testcontainer; SQLite and EF Core InMemory are not permitted substitutes.
- The story gate is the rebuild solution filter using the VSTest runner. Generated code and hosting-only wiring may be excluded from coverage, but application logic, persistence behavior, delivery classification, ordering, and trace propagation require direct tests.

## Technical Decisions

- PaymentsAPI follows ports and adapters. Application handlers and processor strategies depend on repository ports, `ICoreBankApiClient`, `TimeProvider`, and logging; EF Core, HTTP, Dapr, and other network or storage concerns remain in adapters.
- Both PaymentsAPI processors derive from the shared `OutboxProcessorBase` or `InboxProcessorBase`. Polling, partition fan-out, distributed locking, batch claiming, stale-claim recovery, retry/poison transitions, and trace restoration must not be reimplemented in the service.
- Idempotent storage uses a database uniqueness constraint plus `StoreIfNewAsync`; check-then-insert is forbidden. Payments Outbox dedupes on the payment key, while Payments Inbox dedupes on the composite event identity.
- CoreBankAPI owns a checked-in OpenAPI contract covering every public account and transaction operation. A repository-pinned Kiota version generates the transport client before compilation into the intermediate build output. Generated files are neither committed nor exposed to application code.
- `ICoreBankApiClient` is the only application-owned CoreBank transport boundary. Its Kiota-backed adapter resolves Aspire's logical `corebank-api` endpoint, maps generated models into application-owned results, propagates W3C trace headers, and applies the common delivery-outcome classification. No hand-written parallel client, Dapr service invocation path, or `Features:UseDapr` switch remains.
- Message statuses describe transport state only: `Pending`, `Processing`, `Completed`, or `Failed`. A downstream business rejection is a successfully delivered transaction outcome, not a transport failure to retry.
- Distributed locking uses renewable leases through the shared Aspire-managed Redis connection. Lock names remain store-specific so the payments outbox and payments inbox never contend through a shared lock namespace.
- PostgreSQL is the persistence engine. Repository code stays provider-agnostic except for minimal, isolated PostgreSQL-specific operations whose semantics require direct integration coverage.

## Cross-Story Dependencies

- Epic 5 depends on the established test infrastructure, messaging kernel, ServiceDefaults ports and telemetry wiring, and the frozen CoreBankAPI contract. CoreBankAPI and PaymentsAPI work may overlap once those shared prerequisites and stable contracts exist.
- The forwarding processor depends on the generated-client adapter and the payment outbox model. Event handling depends on idempotent subscription intake and the shared inbox processor machinery.
- Later orchestration and acceptance work depends on these service seams remaining stable. Full replicated-topology, cross-instance ordering, no-loss, exactly-once, and end-to-end trace proof belongs to the Aspire and load-test tiers; Epic 5 must provide the ports, persistence semantics, and tests those tiers exercise.
