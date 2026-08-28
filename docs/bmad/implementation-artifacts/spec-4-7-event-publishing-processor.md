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

### 2026-08-28 — Second review patches

Create and asynchronously dispose partition scopes only after lock acquisition; make `EventOccurredAt` compile-time required; convert local occurrence times to UTC; assert the concrete lock namespace; remove dead lock-renew settings. Deferred persistent-schema reset, production composition-root testing, and stale Story 4.6 sprint tracking.

## Design Notes

The kernel owns scopes where partition fan-out begins: one scope per partition isolates its store/`DbContext` while preserving sequential processing.

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: full suite green and CoreBankAPI line coverage at least 90%.
- `git diff --stat HEAD` -- expected: changes limited to Story 4.7 CoreBankAPI files, shared outbox kernel/tests, sprint status, and this spec.

## Suggested Review Order

**Partition-scoped kernel**

- Acquire the distributed lock before creating an asynchronously disposed partition scope.
  [`OutboxProcessorBase.cs:153`](../../../CoreBankDemo.Messaging/OutboxProcessorBase.cs#L153)

- Resolve store and strategy together for sequential work within that partition.
  [`OutboxProcessorBase.cs:194`](../../../CoreBankDemo.Messaging/OutboxProcessorBase.cs#L194)

**Event mapping and stable time**

- Persist the inbox processing stamp independently from mutable claim timestamps.
  [`OutboxEventEnqueuer.cs:105`](../../../CoreBankDemo.CoreBankAPI/Outbox/OutboxEventEnqueuer.cs#L105)

- Map stored event types and stable occurrence time through the publisher port.
  [`DaprOutboxDeliveryStrategy.cs:14`](../../../CoreBankDemo.CoreBankAPI/Outbox/DaprOutboxDeliveryStrategy.cs#L14)

**Hosting**

- Keep the hosted processor limited to options and lock namespace.
  [`MessagingOutboxProcessor.cs:9`](../../../CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxProcessor.cs#L9)

- DI wires scoped repository, delivery strategy, and hosted processor.
  [`Program.cs:55`](../../../CoreBankDemo.CoreBankAPI/Program.cs#L55)

**Verification**

- Prove distinct async-disposed scopes and zero scopes when locks are unavailable.
  [`OutboxProcessorBaseTests.cs:314`](../../../tests/CoreBankDemo.Messaging.Tests/OutboxProcessorBaseTests.cs#L314)

- Prove stable retry payloads and exact CloudEvent mappings.
  [`DaprOutboxDeliveryStrategyTests.cs:152`](../../../tests/CoreBankDemo.CoreBankAPI.Tests/DaprOutboxDeliveryStrategyTests.cs#L152)

- Prove transport and mapping failures remain kernel-owned retry outcomes.
  [`MessagingOutboxProcessorTests.cs:86`](../../../tests/CoreBankDemo.CoreBankAPI.Tests/MessagingOutboxProcessorTests.cs#L86)
