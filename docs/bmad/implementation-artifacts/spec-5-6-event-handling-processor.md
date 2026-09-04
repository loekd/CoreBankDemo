---
title: 'Story 5.6: Event handling processor'
type: 'feature'
created: '2026-08-29'
status: 'done'
review_loop_iteration: 1
baseline_commit: '9630c3e514adc555b12f6fe1d82c8e164f693cdc'
followup_review_recommended: true
deferred:
  - summary: >-
      Partition workload failures can be logged as distributed-lock failures.
    evidence: |-
      InboxProcessorBase catches exceptions around ExecuteWithLockAsync, including exceptions raised by the workload while resolving the scoped store or claiming rows, and labels them "Lock service failed."
    location: >-
      CoreBankDemo.Messaging/InboxProcessorBase.cs
    severity: medium
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-5-context.md'
  - '{project-root}/docs/bmad/implementation-artifacts/spec-5-5-event-subscription-intake.md'
  - '{project-root}/.claude/skills/conventions/SKILL.md'
  - '{project-root}/.claude/skills/messaging-patterns/SKILL.md'
  - '{project-root}/.claude/skills/observability/SKILL.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Story 5.5 durably stores transaction-completed, transaction-failed, and balance-updated events, but PaymentsAPI never drains that inbox. The end-to-end payment trace therefore stops at event intake and the demo never observes the returned CoreBank outcome.

**Approach:** Add a concrete `InboxProcessorBase<InboxMessage>` and scoped `TransactionEventHandler` that dispatches stored payloads by the shared CloudEvent type, emits the approved structured log and tags on the kernel-restored consumer span, then lets the kernel own completion, retry, poison handling, locking, and ordering.

## Boundaries & Constraints

**Always:** Reuse `InboxProcessorBase<InboxMessage>` unchanged with lock prefix `payments-inbox` and map validated `InboxProcessingOptions` into `InboxProcessorOptions`. Register the existing `InboxMessageRepository` as `IInboxMessageStore<InboxMessage>`, the event handler as scoped `IInboxMessageHandler<InboxMessage>`, and the processor as a hosted service. Dispatch only `Constants.TransactionCompleted`, `Constants.TransactionFailed`, and `Constants.BalanceUpdated`; deserialize the matching frozen shared event record from `InboxMessage.Payload`. Enrich `Activity.Current`, which is the consumer span restored by the kernel from persisted `TraceParent`/`TraceState`; do not create a second `ActivitySource`. Completed and balance events log at Information, failed events at Warning, with structured transaction/event/account/status/balance/error fields as applicable.

**Ask First:** Any change to the messaging kernel, shared CloudEvent records/constants, inbox schema, retry policy, lock prefix, or the observational-only behavior.

**Never:** Reimplement polling, partition fan-out, locking, claiming, retry, poison classification, completion, or trace restoration. Never mutate payment or account state, call an external service, swallow malformed/unknown payloads, or mark rows directly. A malformed payload or unsupported stored event type throws so the kernel records retry and eventually terminal failure.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|---------------|----------------------------|----------------|
| Completed | Valid completed payload | Information log; tags transaction, type, status; row completes | N/A |
| Failed | Valid failed payload, nullable reason | Warning log; tags transaction, type, status, reason; row completes | Null reason remains valid |
| Balance update | Valid balance payload | Information log; tags transaction, type, account, delta, new balance, currency; row completes | N/A |
| Duplicate/redelivery row | Kernel reprocesses a claimed event | Observational handler repeats safely; no local state changes | Kernel owns completion |
| Malformed payload | Known type, invalid JSON or JSON `null` | Handler throws; row returns to Pending with error, then Failed at retry limit | Never acknowledge as completed |
| Unsupported stored type | Event type not in the three shared constants | Handler throws explicit unsupported-type error | Kernel retry/poison path |
| Host cancellation | Cancellation during dispatch | Cancellation propagates unchanged; row remains Processing for stale reclaim | No retry increment |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.Messaging/InboxProcessorBase.cs` -- read-only kernel: partition fan-out, distributed locks, oldest-first claims, per-message scopes, restored consumer span, completion, retry, cancellation, and poison transitions.
- `CoreBankDemo.Messaging/IInboxMessageHandler.cs` -- read-only scoped handler contract; normal return means success and any non-cancellation exception enters kernel retry.
- `CoreBankDemo.CoreBankAPI/Inbox/InboxProcessor.cs` -- sibling concrete processor pattern for options mapping and lock-prefix-only specialization.
- `CoreBankDemo.PaymentsAPI/Inbox/{InboxMessage,InboxMessageRepository}.cs` -- Story 5.5 row and dual-role repository; expose the existing instance through the kernel store port.
- `CoreBankDemo.PaymentsAPI/Handlers/TransactionEventHandler.cs` (new) -- scoped dispatch/deserialization and observational logs/tags for the three shared event contracts.
- `CoreBankDemo.PaymentsAPI/Inbox/InboxProcessor.cs` (new) -- concrete kernel processor with `payments-inbox` lock prefix.
- `CoreBankDemo.PaymentsAPI/TransactionEventIntakeServiceCollectionExtensions.cs` and `Program.cs` -- extend existing inbox registration with store/handler/hosted-service wiring; reuse the service-name `ActivitySource`.
- `tests/CoreBankDemo.PaymentsAPI.Tests/{TransactionEventHandlerTests,InboxProcessorTests,TransactionEventProcessorWiringTests}.cs` (new) -- matrix coverage, restored-parent span proof, real store transitions, lock prefix, ordering/isolation, and production composition.
- `tests/CoreBankDemo.Messaging.Tests/InboxProcessorBaseTests.cs` -- read-only proof for generic fan-out, ordering, cancellation, retry, and poison mechanics; Story 5.6 tests only PaymentsAPI specialization and integration.

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.PaymentsAPI/Handlers/TransactionEventHandler.cs` -- implement constant-based typed dispatch, strict deserialization, structured logging, and current-span tags without side effects.
- [x] `CoreBankDemo.PaymentsAPI/Inbox/InboxProcessor.cs` -- add the kernel-derived processor and exact options mapping.
- [x] `CoreBankDemo.PaymentsAPI/TransactionEventIntakeServiceCollectionExtensions.cs` and `Program.cs` -- expose the repository through `IInboxMessageStore`, register the scoped handler, and start the hosted processor.
- [x] `tests/CoreBankDemo.PaymentsAPI.Tests/{TransactionEventHandlerTests,InboxProcessorTests,TransactionEventProcessorWiringTests}.cs` -- cover every matrix row and the actual production registration sequence.

**Acceptance Criteria:**
- Given a pending row for any supported event type, when the PaymentsAPI inbox processor claims it, then the frozen payload is dispatched to the matching observational behavior, the kernel-restored consumer span receives the event-specific tags, the expected structured log is emitted, and the row becomes `Completed` without local business-state mutation.
- Given malformed JSON, JSON `null`, or an unsupported stored type, when processing runs, then the handler throws and the kernel records the normal retry transition, reaching `Failed` only at `MaxRetryCount`.
- Given persisted W3C trace context, when the hosted processor dispatches the event, then the processing span is a consumer child of that context and the handler enriches that same span rather than creating an unrelated trace.
- Given `dotnet test CoreBankDemo.Rebuild.slnf`, when Story 5.6 is complete, then all rebuild tests pass and PaymentsAPI remains at or above 90% line coverage.

## Spec Change Log

- 2026-08-29: During implementation, strict host scope validation exposed a singleton-to-scoped captive dependency in `InboxProcessorBase<TMessage>`. The user explicitly approved crossing the Ask First boundary to correct the shared kernel. The processor now resolves one scoped store per partition and uses that instance consistently for claim, completion, and retry while retaining a separate handler scope per message.

## Design Notes

The handler deliberately combines stored-type dispatch and observational behavior behind `IInboxMessageHandler<InboxMessage>`. This matches the kernel's per-message scoped seam and prevents a second application interface or processor-owned dispatch path. Event-type matching uses wire constants stored by Story 5.5, not CLR type names from the legacy implementation.

The scoped-store correction is infrastructure-only: it removes the invalid hosted-service lifetime graph without changing polling, locking, claim order, retry classification, poison handling, cancellation, trace restoration, or handler scope behavior. CoreBankAPI uses the corrected constructor contract as the existing sibling consumer.

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf --no-restore` -- 639 passed, one pre-existing live-Redis test skipped, zero failed. PaymentsAPI line coverage: 100%; Messaging: 93.28%; CoreBankAPI: 98.57%.
- `git diff --check` -- passed with no whitespace errors.

## Review Results

The configured adversarial, edge-case, and verification-gap lenses reviewed the complete diff from baseline `9630c3e514adc555b12f6fe1d82c8e164f693cdc`. The implementation was corrected to:

- bind event JSON with web conventions and reject missing or explicitly null non-nullable constructor fields;
- include `EventType` in structured event logs and add the required idempotency/partition/event correlation scope;
- preserve an explicit error-reason span tag for valid null failure reasons;
- prove exact W3C parent span restoration, consumer span kind, and real-handler enrichment;
- prove terminal failure at `MaxRetryCount`;
- assert the real `Program` entry point registers the hosted processor and scoped handler; and
- keep real-entry-point intake tests deterministic with a non-acquiring lock double while retaining the hosted processor registration.

A final high-confidence review after these corrections reported no findings. Suggestions concerning pre-existing kernel span status, cancellation logging, and lock-error wording were not applied because they are outside Story 5.6 and outside the user's approved shared-kernel lifetime correction.

## Review Triage Log

### 2026-08-30 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 11: (high 0, medium 6, low 5)
- defer: 1: (high 0, medium 1, low 0)
- reject: 10: (high 0, medium 5, low 5)
- addressed_findings:
  - `[medium]` `[patch]` Reject explicit JSON nulls for non-nullable shared event fields through nullable-annotation-aware deserialization.
  - `[medium]` `[patch]` Remove the post-observation cancellation check that could retry an event after its log and tags were already emitted.
  - `[medium]` `[patch]` Assert every required structured event-log field and value.
  - `[medium]` `[patch]` Exercise invalid JSON and JSON `null` through the real PaymentsAPI processor retry transition.
  - `[medium]` `[patch]` Verify concrete lock-expiry and polling-interval option mapping.
  - `[medium]` `[patch]` Prove the real `Program` entry point accepts and drains an event through the hosted processor.
  - `[low]` `[patch]` Model redelivery with a `Processing` row in the direct handler test.
  - `[low]` `[patch]` Model host cancellation with a `Processing` row in the direct handler test.
  - `[low]` `[patch]` Register the DI lifetime regression processor through `AddHostedService`.
  - `[low]` `[patch]` Eliminate the undisposed service provider from the DI lifetime regression test.
  - `[low]` `[patch]` Correct the review note about event JSON casing.

## Auto Run Result

**Status:** done

**Summary:** Implemented the PaymentsAPI transaction-event inbox processor and observational handler for completed, failed, and balance-updated events. Corrected the shared inbox kernel's hosted-service lifetime graph by resolving one scoped store per partition, retained per-message handler scopes, and preserved retry, poison, cancellation, ordering, and trace restoration behavior.

**Files changed:**
- `CoreBankDemo.PaymentsAPI/Handlers/TransactionEventHandler.cs` — added strict typed event dispatch, structured logs/scopes, and consumer-span enrichment.
- `CoreBankDemo.PaymentsAPI/Inbox/InboxProcessor.cs` — added the `payments-inbox` hosted processor and validated option mapping.
- `CoreBankDemo.PaymentsAPI/Program.cs` and `TransactionEventIntakeServiceCollectionExtensions.cs` — registered the scoped store port, handler, and hosted processor.
- `CoreBankDemo.Messaging/InboxProcessorBase.cs` — resolved the store from a per-partition scope to remove the singleton-to-scoped captive dependency.
- `CoreBankDemo.CoreBankAPI/Inbox/InboxProcessor.cs` — adapted the existing consumer to the corrected kernel constructor.
- `tests/CoreBankDemo.PaymentsAPI.Tests/*TransactionEvent*Tests.cs` and `InboxProcessorTests.cs` — covered all event branches, malformed payloads, retries, poison transition, trace restoration, configuration mapping, and real-host processing.
- `tests/CoreBankDemo.Messaging.Tests/InboxProcessorBaseTests.cs` and `tests/CoreBankDemo.CoreBankAPI.Tests/InboxProcessorTests.cs` — preserved and expanded shared-kernel and sibling-consumer regression coverage.
- `docs/bmad/implementation-artifacts/epic-5-context.md` — refreshed Epic 5 implementation context.
- `docs/bmad/implementation-artifacts/sprint-status.yaml` — synchronized Story 5.6 workflow status.

**Review findings:** 11 patches applied, one pre-existing diagnostic issue deferred, and 10 duplicate, out-of-contract, or non-actionable findings rejected.

**Follow-up review recommendation:** true — patched findings: high 0, medium 6, low 5; score `3 × 6 + 5 = 23`.

**Verification:** `dotnet test CoreBankDemo.Rebuild.slnf --no-restore` passed 639 tests with one pre-existing live-Redis skip and zero failures; PaymentsAPI line coverage is 100%. `git diff --check` passed.

**Residual risks:** The generic kernel can still label workload/store failures as lock-service failures; this pre-existing diagnostic issue is deferred. The live Redis lease test remains skipped by its existing environment gate.
