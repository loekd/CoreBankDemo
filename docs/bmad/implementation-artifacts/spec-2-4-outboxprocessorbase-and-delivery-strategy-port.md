---
title: 'Story 2.4: OutboxProcessorBase and delivery strategy port'
type: 'feature'
created: '2026-08-21'
status: 'done'
baseline_commit: '691f24631b62ade495a5c1bbc12d244d3dc1dce1'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-2-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Every outbox needs the identical poll/lock/dispatch loop with delivery pluggable — the legacy defect (AD-2 violation, ADR A2) was `MessagingOutboxProcessor` bypassing the shared base entirely and re-implementing its own loop (FR-5, FR-6, FR-8; AD-3, AD-8, AD-11).

**Approach:** Add `MarkAsCompletedAsync`/`FindByIdAsync` to `MessageRepositoryBase`; define `IOutboxMessageStore<TMessage>` (the narrow port `OutboxProcessorBase` depends on — never the concrete EF class, so the processor is Moq-testable per AD-2/AD-9) implemented by `OutboxMessageRepositoryBase`; define `IOutboxDeliveryStrategy<TMessage>` (single method, throws on failure — success/failure classification is the processor's job, never the strategy's, per AD-11); build `OutboxProcessorBase<TMessage>` as a `BackgroundService` reusing the old `CoreBankDemo.ServiceDefaults.IDistributedLockService` (kernel already references ServiceDefaults) until epic 3 rebuilds it.

## Boundaries & Constraints

**Always:** `OutboxProcessorBase` depends only on `IOutboxMessageStore<TMessage>`, `IDistributedLockService`, `IOutboxDeliveryStrategy<TMessage>`, `ActivitySource` (ctor-injected, never `new`'d inside), `TimeProvider`, `ILogger` — never a concrete `DbContext`; strategy success → `MarkAsCompletedAsync`; strategy throws → `MarkAsFailedWithRetryAsync` (kernel decides, never the strategy); one partition-lock per partition per tick, no partition processed by two ticks concurrently; span restored from stored `TraceParent`, tags include `IdempotencyKey` and `PartitionId`.

**Ask First:** Whether processor settings (partition count, lock expiry seconds, polling interval) come via a small primitive-typed record defined in this story vs. referencing old `ServiceDefaults` options types (which epic 3 will demolish) — default to a local record to avoid coupling to soon-to-be-deleted types.

**Never:** Claiming/retry logic itself (story 2.3, reused not reimplemented); concrete delivery implementations (HTTP/Dapr — epics 4/5); `InboxProcessorBase` (story 2.5).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Tick | Mocked store returns claimed messages across partitions | Fans out over all `PartitionCount` partitions in parallel via the lock service | N/A |
| Lock held | Partition lock acquired | Claim + dispatch runs inside the lock scope | N/A |
| Lock not acquired | `ExecuteWithLockAsync` returns false | Partition skipped silently this tick, no throw | N/A |
| Delivery success | Strategy completes normally | `MarkAsCompletedAsync` called | N/A |
| Delivery failure | Strategy throws any exception | `MarkAsFailedWithRetryAsync` called with the error message, exception not rethrown | tick continues to next message |
| Cancellation mid-dispatch | Token cancelled during a partition's work | In-flight dispatch stops promptly, no message lost or double-completed | N/A |
| Tick-level exception | Store/lock throws outside message dispatch | Logged, tick survives, next tick still scheduled | swallowed at tick level |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.Messaging/MessageRepositoryBase.cs` (story 2.3, committed 691f246) — add `MarkAsCompletedAsync(TMessage, ct)` (Status=Completed, ProcessedAt=now via `TimeProvider`) and `FindByIdAsync(Guid, ct)`
- `CoreBankDemo.ServiceDefaults/IDistributedLockService.cs` — existing old interface, reused as-is per epic context (`Task<bool> ExecuteWithLockAsync(string, int, Func<CancellationToken,Task>, CancellationToken)`)
- New: `CoreBankDemo.Messaging/IOutboxMessageStore.cs`, `IOutboxDeliveryStrategy.cs`, `OutboxProcessorBase.cs`
- `CoreBankDemo.Messaging/OutboxMessageRepositoryBase.cs` — implement `IOutboxMessageStore<TMessage>`
- Epic context §"Processor bases — loop shape" and its listed violations (registered ActivitySource not `new`'d, `PartitionId` tag, no reimplemented retry/claim logic)

## Tasks & Acceptance

**Execution:**
- [x] `MessageRepositoryBase`: `MarkAsCompletedAsync`, `FindByIdAsync` — tests first (SQLite)
- [x] `IOutboxMessageStore<TMessage>` port + `OutboxMessageRepositoryBase` implementation
- [x] `IOutboxDeliveryStrategy<TMessage>` — `Task DeliverAsync(TMessage message, CancellationToken ct)`
- [x] `OutboxProcessorBase<TMessage>` — TDD via Moq on all four ports/deps, covering the full I/O matrix

**Acceptance Criteria:**
- Given a mocked store/lock/strategy, when one tick runs, then every partition is attempted under its own lock name `<prefix>-partition-<id>` and results match the matrix
- Given two concurrent tick invocations sharing a lock mock that serializes by lock name, when both run, then no partition's workload executes concurrently
- Given a delivery exception, when it propagates from the strategy, then the processor never lets it escape the tick (logged, retry path invoked, loop continues)

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — expected: green, Messaging ≥90%

## Spec Change Log

- 2026-08-22 (step-04): review caught a real AD-11 violation: ProcessMessageAsync's single try/catch treated a MarkAsCompletedAsync failure (after DeliverAsync already succeeded) as a delivery failure, misrouting it through MarkAsFailedWithRetryAsync — burning a retry and risking redelivery/eventual terminal-Failed for a message that already succeeded. Fixed: split into separate try/catch scopes; completion-persistence failures log distinctly and are NOT retried through the delivery-failure path, left Processing to be safely reclaimed via story 2.3's stale-claim mechanism. Also: MarkAsCompletedAsync now no-ops on already-terminal messages (mirrors MarkAsFailedWithRetryAsync); null claim-batch guarded; PollingInterval validated fail-fast; ActivityKind.Producer asserted. 121 Messaging tests, 94.49% line / 80.85% branch, gate live at 90%.
