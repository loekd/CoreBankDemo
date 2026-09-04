---
title: 'Story 5.5: Event subscription intake'
type: 'feature'
created: '2026-08-29'
status: 'done'
review_loop_iteration: 1
followup_review_recommended: false
baseline_commit: '312ce8e1b6aa81269fd07c46dfdc09566d11595b'
baseline_revision: '312ce8e1b6aa81269fd07c46dfdc09566d11595b'
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-5-context.md'
  - '{project-root}/docs/bmad/implementation-artifacts/spec-5-4-forwarding-processor.md'
  - '{project-root}/.claude/skills/conventions/SKILL.md'
  - '{project-root}/.claude/skills/messaging-patterns/SKILL.md'
  - '{project-root}/.claude/skills/observability/SKILL.md'
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** Dapr already routes `transaction-events` deliveries to PaymentsAPI, and the event inbox schema already exists, but no rebuilt HTTP surface or repository stores those deliveries. Broker retries therefore cannot be acknowledged through durable, idempotent intake.

**Approach:** Restore the four frozen event routes as thin controller actions backed by a typed intake handler and a concrete kernel inbox repository. Persist each known shared CloudEvent contract before returning success; acknowledge duplicate and unknown deliveries without creating extra rows.

## Boundaries & Constraints

**Always:** Keep routes `/events/transactions/completed`, `/failed`, `/balance-updated`, and `/unknown` aligned with both Dapr subscription manifests. Use the shared `TransactionCompletedEvent`, `TransactionFailedEvent`, `BalanceUpdatedEvent`, and `Constants` values. Store transaction-wide events with `AccountNumber = ""`; store balance events with their account number. Database identity remains `(TransactionId, EventType, AccountNumber)`. Set `IdempotencyKey = TransactionId`, derive the partition from that exact key through `PartitionHelper`, serialize the typed payload, stamp `Pending`, injected UTC time, and ambient trace context, and use `InboxMessageRepositoryBase.StoreIfNewAsync` for insert-first dedupe. Known new and duplicate deliveries both return HTTP 200; non-unique persistence failures and cancellation propagate so Dapr can retry. Unknown types log a structured warning and return 200 without storage.

**Block If:** A change is required to the frozen routes, shared CloudEvent contracts/constants, existing composite identity, or Messaging repository kernel.

**Never:** Process event business behavior in this story (Story 5.6 owns dispatch), implement check-then-insert dedupe, add custom polling/locking, mutate local payment state, call the clock directly, or put persistence/partition/serialization logic in the controller.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Completed event | Valid shared contract | One pending row; empty account sentinel; HTTP 200 | Infrastructure errors propagate |
| Failed event | Error reason present or null | Payload preserves the nullable reason; one pending row; HTTP 200 | Infrastructure errors propagate |
| Balance event | Same transaction, either account | One row per distinct account; HTTP 200 | Infrastructure errors propagate |
| Broker redelivery | Same transaction/type/account | Existing identity wins; no second row; HTTP 200 and duplicate log | Unique violation is acknowledged |
| Distinct event identity | Same transaction with another type/account | Stored independently | No error expected |
| Unknown CloudEvent type | Dapr default route | No row; warning; HTTP 200 | Deliberately acknowledged |
| No ambient activity | Any known event | Null trace fields are persisted | No error expected |

</intent-contract>

## Code Map

- `dapr/components/subscription-transaction-events.yaml` and `dapr/components-loadtest/subscription-transaction-events.yaml` -- read-only external route/type contract; both target the four controller paths.
- `CoreBankDemo.ServiceDefaults/CloudEventTypes/{Constants,TransactionCompletedEvent,TransactionFailedEvent,BalanceUpdatedEvent}.cs` -- read-only frozen event names and payload records.
- `CoreBankDemo.PaymentsAPI/Inbox/InboxMessage.cs` and `PaymentsDbContext.cs` -- existing pending-row shape and database-enforced `(TransactionId, EventType, AccountNumber)` identity; empty account is the transaction-event sentinel.
- `CoreBankDemo.Messaging/{InboxMessageRepositoryBase,MessageRepositoryBase,PartitionHelper}.cs` -- read-only repository, race-safe dedupe, and partition primitives.
- `CoreBankDemo.PaymentsAPI/Inbox/InboxMessageRepository.cs` (new) -- concrete `InboxMessageRepositoryBase<InboxMessage, PaymentsDbContext>` and narrow intake store port.
- `CoreBankDemo.PaymentsAPI/Handlers/TransactionEventIntakeHandler.cs` (new) -- typed overloads map known contracts to rows, serialize payloads, capture time/trace, and log scoped identity/duplicate details.
- `CoreBankDemo.PaymentsAPI/Controllers/TransactionEventsController.cs` (new) -- thin four-route HTTP adapter returning 200 after handler completion.
- `CoreBankDemo.PaymentsAPI/TransactionEventIntakeServiceCollectionExtensions.cs`, `Program.cs`, and `appsettings.json` -- testable controllers+Dapr, repository/handler, inbox options, CloudEvents, and route wiring; no processor registration yet.
- `tests/CoreBankDemo.PaymentsAPI.Tests/{TransactionEventIntakeHandlerTests,TransactionEventsControllerTests}.cs` -- exact mapping/token/log assertions and controller completion/warning behavior.
- `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/{InboxMessageRepositoryTests,TransactionEventIntakeWiringTests}.cs` -- PostgreSQL repository race behavior and structured CloudEvent POSTs through the production entry point proving Dapr unwrapping, production DI/middleware/routing, HTTP 200, and durable storage together.
- `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/PaymentsApiTestSupport.cs` -- reuse the shared PostgreSQL Testcontainer database fixture.

## Tasks & Acceptance

**Execution:**
- `CoreBankDemo.PaymentsAPI/Inbox/InboxMessageRepository.cs` -- add the concrete kernel repository and narrow `StoreIfNewAsync` port.
- `CoreBankDemo.PaymentsAPI/Handlers/TransactionEventIntakeHandler.cs` -- implement typed event-to-row mapping, serialization, partition/time/trace capture, and structured duplicate logging.
- `CoreBankDemo.PaymentsAPI/Controllers/TransactionEventsController.cs` -- expose the frozen known-event and default unknown routes as thin actions.
- `CoreBankDemo.PaymentsAPI/TransactionEventIntakeServiceCollectionExtensions.cs`, `Program.cs`, `appsettings.json` -- register validated inbox options and intake dependencies, add Dapr controller integration, and enable CloudEvent/controller routing.
- `tests/CoreBankDemo.PaymentsAPI.Tests/{TransactionEventIntakeHandlerTests,TransactionEventsControllerTests}.cs` -- cover event mapping, exact cancellation-token forwarding, structured duplicate/unknown logging, and storage-before-controller-completion behavior.
- `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/{InboxMessageRepositoryTests,TransactionEventIntakeWiringTests}.cs` -- cover PostgreSQL dedupe and post structured CloudEvents through the real PaymentsAPI entry point, asserting production middleware/DI/routing, duplicate HTTP 200 responses, and one durable row.

**Acceptance Criteria:**
- Given Dapr posts any supported `transaction-events` payload to its configured route, when PaymentsAPI accepts it, then HTTP 200 is returned only after the correctly typed pending inbox row is durably stored with composite identity, partition, timestamp, and trace context.
- Given Dapr redelivers the same supported event concurrently or sequentially, when both requests complete, then both return HTTP 200 and exactly one `(TransactionId, EventType, AccountNumber)` row exists.
- Given one transaction produces completion/failure and two account-balance identities, when those events arrive, then each distinct type/account identity can coexist while exact redeliveries dedupe.
- Given an unsupported CloudEvent reaches the subscription default route, when PaymentsAPI receives it, then HTTP 200 is returned, no inbox row is created, and a warning is logged.
- Given `dotnet test CoreBankDemo.Rebuild.slnf`, when Story 5.5 is complete, then all rebuild tests pass and PaymentsAPI remains at or above its coverage threshold.

## Spec Change Log

- **Follow-up review (2026-08-30):** forwarded CloudEvent type, id, and source through Dapr middleware and included them as structured fields in unknown-event warnings. Strengthened both subscription-manifest assertions so each event-type expression must remain paired with its exact route; swapping two paths now fails verification. Updated Story 5.5's test map from the retired SQLite unit fixture to the PostgreSQL integration tier introduced by ADR-016.
- **Review loop 1:** verification and intent-alignment reviewers found that the original plan reconstructed registrations and inspected route metadata but never sent a CloudEvent through the production PaymentsAPI entry point. Amended the Code Map and test task to require real-entry-point structured CloudEvent posts with infrastructure overridden by test doubles, including duplicate HTTP acknowledgements and durable-row assertions. This avoids a green suite when `Program.cs` omits Dapr middleware, routing, or intake DI. Also carried forward the lower verification patches so re-derivation pins every event's identity/partition mapping, exact cancellation-token forwarding, structured duplicate and unknown-warning logging, and delayed controller completion. **KEEP:** the thin controller, typed handler, kernel repository, shared constants/contracts, approved composite identity, transaction-based partitioning, injected time, ambient trace capture, insert-first dedupe, and Story 5.6 processing boundary.

## Review Triage Log

### 2026-08-30 — Follow-up review pass
- intent_gap: 0
- bad_spec: 0
- patch: 2 (high 1, medium 1, low 0)
- defer: 0
- addressed_findings:
  - `[high]` `[patch]` Replaced independent manifest substring checks with event-type/route pair assertions, so swapped routes cannot pass.
  - `[medium]` `[patch]` Forwarded and logged unknown CloudEvent type, id, and source as structured fields instead of emitting a fixed warning with no event identity.
- verification:
  - Full rebuild gate passed.
  - Focused PaymentsAPI and PostgreSQL Story 5.5 tests passed.
  - `git diff --check` passed.
- followup_review_recommended: false

### 2026-08-29 — Review pass
- intent_gap: 0
- bad_spec: 1: (high 1, medium 0, low 0)
- patch: 5: (high 0, medium 3, low 2)
- defer: 0
- reject: 11: (high 0, medium 5, low 6)
- addressed_findings:
  - `[high]` `[bad_spec]` Replace reconstructed DI/route-metadata evidence with a structured CloudEvent POST through the production entry point, proving the deployed Dapr HTTP and persistence surface before re-deriving the implementation.

### 2026-08-29 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 8: (high 2, medium 3, low 3)
- defer: 0
- reject: 14: (high 0, medium 8, low 6)
- addressed_findings:
  - `[high]` `[patch]` Added real-entry-point structured CloudEvent coverage for failed and balance-updated routes so routing, Dapr unwrapping, typed binding, and durable composite identities cannot regress independently of completed-event intake.
  - `[high]` `[patch]` Added handler and controller failure-path tests proving persistence exceptions and cancellation are never converted into successful acknowledgements.
  - `[medium]` `[patch]` Added startup-validator coverage for missing, three, and five inbox partitions, preserving the exact-four topology invariant.
  - `[medium]` `[patch]` Isolated process-global connection settings from parallel tests and restored their prior values after each real-entry-point fixture.
  - `[medium]` `[patch]` Removed the unrelated outbox hosted service from the HTTP fixture so verification never contacts a local Redis instance.
  - `[low]` `[patch]` Removed compile-time dependencies on unsupported EF Core internal interfaces while retaining pooled-provider replacement.
  - `[low]` `[patch]` Made failed and balance controller tests hold storage incomplete before asserting that HTTP completion waits.
  - `[low]` `[patch]` Added a guard that both declarative Dapr subscription manifests retain all shared event-type and route mappings.

## Design Notes

`IdempotencyKey` remains the bounded transaction identifier while the approved composite index owns event uniqueness. This avoids concatenated legacy keys exceeding the schema's 100-character limit and keeps all events for one transaction on the same partition; `EventType` stores the shared CloudEvent constant rather than a CLR type name.

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: all tests pass and enforced coverage thresholds hold.
- `git diff --check` -- expected: no whitespace errors.

## Auto Run Result

Status: blocked
Blocking condition: finalization left repository dirty

**Summary:** Added durable, idempotent PaymentsAPI intake for the three supported `transaction-events` CloudEvents plus the unknown-event acknowledgement route. Known deliveries now pass through Dapr's real CloudEvents middleware into a typed handler and kernel inbox repository, preserving composite identity, partition, timestamp, payload, and trace context; event processing remains deferred to Story 5.6.

**Files changed:**
- `CoreBankDemo.PaymentsAPI/Controllers/TransactionEventsController.cs` -- four frozen Dapr-facing event routes with storage-before-200 semantics.
- `CoreBankDemo.PaymentsAPI/Handlers/TransactionEventIntakeHandler.cs` -- typed event mapping, serialization, partition/time/trace capture, and structured duplicate logging.
- `CoreBankDemo.PaymentsAPI/Inbox/InboxMessageRepository.cs` -- race-safe kernel repository and narrow intake port.
- `CoreBankDemo.PaymentsAPI/TransactionEventIntakeServiceCollectionExtensions.cs` -- exact-four inbox options validation plus Dapr, repository, and handler registration.
- `CoreBankDemo.PaymentsAPI/Program.cs` and `appsettings.json` -- production CloudEvents middleware, intake composition, testable entry point, and inbox settings.
- `Directory.Packages.props` and `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankDemo.PaymentsAPI.Tests.csproj` -- centrally managed real-entry-point hosting test dependency.
- `tests/CoreBankDemo.PaymentsAPI.Tests/InboxMessageRepositoryTests.cs` -- fresh, duplicate, distinct, and concurrent repository behavior.
- `tests/CoreBankDemo.PaymentsAPI.Tests/TransactionEventIntakeHandlerTests.cs` -- complete event mapping, trace, logging, cancellation, and failure matrix.
- `tests/CoreBankDemo.PaymentsAPI.Tests/TransactionEventsControllerTests.cs` -- exact delegation, delayed completion, failure propagation, and unknown warning.
- `tests/CoreBankDemo.PaymentsAPI.Tests/TransactionEventIntakeWiringTests.cs` -- real `Program` structured CloudEvent posts, duplicate acknowledgement, route manifests, and startup validation.

**Review findings:** One high-severity specification gap triggered a full re-derivation. The final pass applied 8 patches (high 2, medium 3, low 3), deferred 0 items, and rejected 14 findings as out-of-scope or unsupported.

**Follow-up review recommendation:** `true`; patched counts were high 2, medium 3, low 3. Score: `3 * 3 + 1 * 3 = 12`, and high-severity patches independently require follow-up.

**Verification:** `dotnet test CoreBankDemo.Rebuild.slnf` passed 615 tests with one pre-existing skipped Redis integration test (616 total); PaymentsAPI coverage was 100% line, 98.21% branch, and 100% method. `git diff --check` passed.

**Residual risks:** Verification does not run a live Dapr sidecar/broker; PostgreSQL repository behavior, the actual ASP.NET entry point, CloudEvent unwrapping, declarative manifest alignment, and all route payloads are covered independently and together.

## Suggested Review Order

**Unknown-event diagnostics**

- Forward CloudEvent identity metadata before Dapr unwraps the payload.
  [`Program.cs:61`](../../../CoreBankDemo.PaymentsAPI/Program.cs#L61)

- Acknowledge unsupported events while preserving type, id, and source in structured logs.
  [`TransactionEventsController.cs:55`](../../../CoreBankDemo.PaymentsAPI/Controllers/TransactionEventsController.cs#L55)

**Regression verification**

- Prove unknown acknowledgements include all diagnostic identity fields.
  [`TransactionEventsControllerTests.cs:79`](../../../tests/CoreBankDemo.PaymentsAPI.Tests/TransactionEventsControllerTests.cs#L79)

- Bind each subscription expression to its exact route in both manifests.
  [`TransactionEventIntakeWiringTests.cs:207`](../../../tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/TransactionEventIntakeWiringTests.cs#L207)
