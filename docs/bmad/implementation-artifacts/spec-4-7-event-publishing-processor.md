---
title: 'Story 4.7: Event publishing processor'
type: 'feature'
created: '2026-08-28'
status: 'done'
baseline_commit: '75d9651dbb6eb1ed5a8c2c29112e568db72a29d1'
review_loop_iteration: 1
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-4-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Story 4.6 enqueues CoreBank domain events, but nothing publishes them, so consumers never receive transaction outcomes or balance updates.

**Approach:** Scope shared-kernel dependencies per partition, then add CoreBank's processor, repository, and publishing strategy. Persist immutable occurrence time at enqueue so retries publish stable payloads.

## Boundaries & Constraints

**Always:** `OutboxProcessorBase<TMessage>` accepts `IServiceScopeFactory`, creates one scope per parallel partition, and resolves that partition's store and strategy from it; claiming, sequential delivery, completion, and retry share the partition scope. Preserve kernel outcomes. CoreBank's processor adds only option translation and lock prefix `messaging-outbox`; production uses four partitions. Every enqueued row requires immutable `EventOccurredAt`, copied from the inbox message's stamped `ProcessedAt`; absence fails rather than inventing time. Publish stored type/source, `subject = TransactionId`, traceparent, and cancellation token. Map completed/failed rows to their frozen records using `EventOccurredAt`, and balance rows using transaction/account/amount/new-balance/currency. Unsupported types or missing balances throw for kernel retry.

**Ask First:** Kernel changes beyond per-partition scoping; schema changes beyond `EventOccurredAt`; changes to publisher signatures, event records/constants, or pubsub/topic names.

**Never:** Call `DaprClient` outside `DaprEventPublisher`; duplicate kernel loop/outcome logic; capture scoped dependencies in the hosted singleton; share a `DbContext` across partitions; swallow delivery errors; publish inside Story 4.6's transaction; modify PaymentsAPI behavior.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|---------------|----------------------------|----------------|
| Completed transaction | Pending completed row | Publish exact payload/metadata with immutable time; kernel completes row | N/A |
| Failed transaction | Pending failed row | Publish exact payload with immutable time and nullable reason; kernel completes row | N/A |
| Balance update | Pending `BalanceUpdated` row with `NewBalance` | Publish exact account/delta/new-balance/currency payload | Missing balance throws and kernel schedules retry |
| Transport failure | `IEventPublisher` throws | No success-shaped return or completion | Exception propagates from strategy; kernel increments retry and records error |
| Unknown event | Unsupported `EventType` | Nothing publishes | Throw; kernel retry path handles it |
| Parallel partitions | Messages in multiple partitions | Distinct scoped store/`DbContext` per partition; in-partition order stays sequential | Partition failures remain isolated |
| Retry | Previously attempted row | Published payload retains the original `EventOccurredAt` | Transport exception propagates; kernel increments retry and records error |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.Messaging/OutboxProcessorBase.cs`; `tests/CoreBankDemo.Messaging.Tests/OutboxProcessorBaseTests.cs` -- per-partition scopes; prove isolation/disposal and preserve outcomes.
- `CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxMessage.cs`; `CoreBankDemo.CoreBankAPI/CoreBankDbContext.cs`; `CoreBankDemo.CoreBankAPI/Outbox/OutboxEventEnqueuer.cs` -- add/configure/populate `EventOccurredAt`; no migration.
- `CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxRepository.cs` -- new kernel store adapter.
- `CoreBankDemo.CoreBankAPI/Outbox/DaprOutboxDeliveryStrategy.cs` -- exact event switch and publisher adapter.
- `CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxProcessor.cs` -- thin scoped-kernel subclass.
- `CoreBankDemo.CoreBankAPI/Program.cs`; `CoreBankDemo.CoreBankAPI/appsettings.json` -- DI and four partitions.
- `tests/CoreBankDemo.CoreBankAPI.Tests/{OutboxEventEnqueuer,DaprOutboxDeliveryStrategy,MessagingOutboxProcessor}Tests.cs` -- matrix and integration coverage.

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.Messaging/OutboxProcessorBase.cs`; `tests/CoreBankDemo.Messaging.Tests/OutboxProcessorBaseTests.cs` -- resolve one store/strategy scope per partition and prove isolation without changing outcomes.
- [x] `CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxMessage.cs`; `CoreBankDemo.CoreBankAPI/CoreBankDbContext.cs`; `CoreBankDemo.CoreBankAPI/Outbox/OutboxEventEnqueuer.cs`; `tests/CoreBankDemo.CoreBankAPI.Tests/OutboxEventEnqueuerTests.cs` -- persist and verify immutable occurrence time.
- [x] `CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxRepository.cs` -- add the CoreBank outbox repository/store adapter.
- [x] `CoreBankDemo.CoreBankAPI/Outbox/DaprOutboxDeliveryStrategy.cs`; `tests/CoreBankDemo.CoreBankAPI.Tests/DaprOutboxDeliveryStrategyTests.cs` -- implement/test every publishing matrix row.
- [x] `CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxProcessor.cs`; `tests/CoreBankDemo.CoreBankAPI.Tests/MessagingOutboxProcessorTests.cs` -- add thin processor; prove completion, retries, and no loop overrides.
- [x] `CoreBankDemo.CoreBankAPI/Program.cs`; `CoreBankDemo.CoreBankAPI/appsettings.json` -- register scoped dependencies/host and set four partitions.

**Acceptance Criteria:**
- Given an outbox row, when claimed, then `IEventPublisher` receives the exact payload, stored metadata/traceparent, and transaction subject before kernel completion.
- Given mapping or transport failure, when delivery runs, then the strategy throws and only the kernel applies retry state.
- Given parallel work, when a tick runs, then each partition resolves/disposes its own scope.
- Given a retry, when republished, then occurrence time is unchanged.
- Given the concrete processor type, when reviewed by reflection, then it overrides only `LockNamePrefix` and contains no custom loop methods.
- Given the full rebuild solution, when tested, then all tests pass and CoreBankAPI retains at least 90% line coverage.

## Spec Change Log

### 2026-08-28 — Human-approved review repair

Review found singleton capture of a scoped EF store and unstable timestamps because claim/retry mutates `CreatedAt`. The human approved per-partition kernel scopes and immutable `EventOccurredAt`. Preserve the thin processor, publisher port, exact mappings, and kernel-owned outcomes.

## Design Notes

The kernel owns scopes where partition fan-out begins: one scope per partition isolates its store/`DbContext` while preserving sequential processing.

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: full suite green and CoreBankAPI line coverage at least 90%.
- `git diff --stat HEAD` -- expected: changes limited to CoreBankAPI story 4.7 files, tests, wiring, and this spec.

## Suggested Review Order

**Partition-scoped kernel**

- Partition fan-out now owns scoped stores and strategies without singleton capture.
  [`OutboxProcessorBase.cs:153`](../../../CoreBankDemo.Messaging/OutboxProcessorBase.cs#L153)

- Isolation test proves four distinct scopes are resolved and disposed.
  [`OutboxProcessorBaseTests.cs:300`](../../../tests/CoreBankDemo.Messaging.Tests/OutboxProcessorBaseTests.cs#L300)

**Event mapping and stable time**

- Strategy maps stored event types into exact frozen event records.
  [`DaprOutboxDeliveryStrategy.cs:14`](../../../CoreBankDemo.CoreBankAPI/Outbox/DaprOutboxDeliveryStrategy.cs#L14)

- Enqueue rejects missing occurrence time and persists the inbox stamp.
  [`OutboxEventEnqueuer.cs:21`](../../../CoreBankDemo.CoreBankAPI/Outbox/OutboxEventEnqueuer.cs#L21)

- Retry coverage proves payload occurrence time survives mutable claim timestamps.
  [`DaprOutboxDeliveryStrategyTests.cs:132`](../../../tests/CoreBankDemo.CoreBankAPI.Tests/DaprOutboxDeliveryStrategyTests.cs#L132)

**Hosting and outcomes**

- Thin processor translates options and fixes the required lock prefix.
  [`MessagingOutboxProcessor.cs:9`](../../../CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxProcessor.cs#L9)

- DI wires scoped repository, delivery strategy, and hosted processor.
  [`Program.cs:56`](../../../CoreBankDemo.CoreBankAPI/Program.cs#L56)

- Integration tests prove kernel-owned completion, retries, and thin inheritance.
  [`MessagingOutboxProcessorTests.cs:21`](../../../tests/CoreBankDemo.CoreBankAPI.Tests/MessagingOutboxProcessorTests.cs#L21)
