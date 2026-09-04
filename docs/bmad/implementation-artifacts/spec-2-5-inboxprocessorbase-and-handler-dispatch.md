---
title: 'Story 2.5: InboxProcessorBase and handler dispatch'
type: 'feature'
created: '2026-08-22'
status: 'done'
baseline_commit: '854ba7391dcb913439a0e24ae13d99cf327d25e6'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-2-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Every inbox needs the same poll/lock/dispatch loop as the outbox (story 2.4), but with per-message handler resolution — the legacy inbox never claimed to Processing and never scoped a handler per message (FR-19; AD-3).

**Approach:** Define `IInboxMessageStore<TMessage>` (mirrors `IOutboxMessageStore<TMessage>`) implemented by `InboxMessageRepositoryBase`; define `IInboxMessageHandler<TMessage>` (single method, throws on failure — same success/failure contract as `IOutboxDeliveryStrategy`, per AD-11 applied symmetrically); build `InboxProcessorBase<TMessage>` mirroring `OutboxProcessorBase`'s loop shape exactly, with one difference: the handler is resolved per message from a fresh `IServiceScopeFactory`-created DI scope (not ctor-injected as a singleton), so each message gets independent scoped dependencies (e.g. a fresh DbContext for whatever the handler does).

## Boundaries & Constraints

**Always:** `InboxProcessorBase` depends only on `IInboxMessageStore<TMessage>`, `IDistributedLockService`, `IServiceScopeFactory`, `ActivitySource`, `TimeProvider`, `ILogger` — never a concrete `DbContext` or a ctor-injected handler instance; one fresh scope per message, disposed after that message's handler call returns or throws; handler success → `MarkAsCompletedAsync`; handler throws → `MarkAsFailedWithRetryAsync` (classification in the kernel, never the handler, mirroring story 2.4's fixed completion-vs-failure separation); span restored from stored `TraceParent`, `ActivityKind.Consumer`, tags include `IdempotencyKey`/`PartitionId`.

**Ask First:** None expected — this story reuses every pattern story 2.4 already established.

**Never:** Reimplement claim/retry/lock/fan-out logic (reuse the story 2.3 repository-base methods and story 2.4's proven loop shape); concrete handlers (epics 4/5); repeat the exact completion-vs-delivery-failure misclassification bug fixed in 2.4 — the same separated-try/catch pattern applies here for handler success followed by a completion-persistence failure.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Tick | Mocked store returns claimed messages across partitions | Fans out over all `PartitionCount` partitions under per-partition locks | N/A |
| Handler success | Handler completes normally | `MarkAsCompletedAsync` called | N/A |
| Handler failure | Handler throws | `MarkAsFailedWithRetryAsync` called, not rethrown | tick continues |
| Completion-persistence failure after handler success | Handler succeeds, `MarkAsCompletedAsync` throws | Logged distinctly, NOT routed through `MarkAsFailedWithRetryAsync` (mirrors 2.4's fix) | left claimed, naturally reclaimed |
| Per-message scoping | Two messages processed in one partition batch | Each gets its own DI scope; scopes don't leak between messages | N/A |
| Cancellation mid-dispatch | Token cancelled during a partition's work | In-flight dispatch stops promptly, message left claimed (reclaimed later), no throw escapes the tick | N/A |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.Messaging/OutboxProcessorBase.cs` (story 2.4, committed 854ba73) — the loop shape to mirror, including the fixed separated try/catch for completion-vs-failure
- `CoreBankDemo.Messaging/IOutboxMessageStore.cs`, `IOutboxDeliveryStrategy.cs`, `OutboxProcessorOptions.cs` — direct pattern source for the new inbox equivalents
- `CoreBankDemo.Messaging/InboxMessageRepositoryBase.cs` — implement `IInboxMessageStore<TMessage>`
- New: `CoreBankDemo.Messaging/IInboxMessageStore.cs`, `IInboxMessageHandler.cs`, `InboxProcessorBase.cs`, `InboxProcessorOptions.cs`
- Epic context §"Processor bases — loop shape": "a fresh DI scope per message, re-resolve repository... call abstract ProcessMessageAsync" — this story's handler-per-scope resolution is the concrete mechanism for that requirement

## Tasks & Acceptance

**Execution:**
- [x] `IInboxMessageStore<TMessage>` port + `InboxMessageRepositoryBase` implementation (should already satisfy it via inherited members, per story 2.4's outbox precedent)
- [x] `IInboxMessageHandler<TMessage>` — `Task HandleAsync(TMessage message, CancellationToken ct)`
- [x] `InboxProcessorOptions` — mirrors `OutboxProcessorOptions` including the PollingInterval fail-fast validation fix from 2.4
- [x] `InboxProcessorBase<TMessage>` — TDD via Moq, per-message `IServiceScopeFactory.CreateScope()` resolving `IInboxMessageHandler<TMessage>`, covering the full I/O matrix

**Acceptance Criteria:**
- Given a mocked store/lock/scope-factory, when one tick runs, then results match the matrix and mirror story 2.4's proven behavior (including the completion-vs-failure separation)
- Given two messages in one claimed batch, when both process, then each resolves its handler from a distinct scope (assert via a scope-factory mock call count or distinct handler instances)
- Given a handler exception, when it propagates, then it never escapes the tick

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — expected: green, Messaging ≥90%

## Spec Change Log

- 2026-08-22 (step-04): review confirmed the mirror is faithful (no reintroduction of 2.4's completion-vs-failure bug). Patched a pre-existing asymmetry present in BOTH processors: MarkAsFailedWithRetryAsync was unguarded in the failure catch, so a store hiccup while recording a failure could abort the rest of a partition's batch for the tick. Fixed in InboxProcessorBase AND OutboxProcessorBase (backport) with its own try/catch, logged distinctly, tick continues. Added a null-claimed-element guard and a scope-disposal-ordering test proving the per-message DI scope is disposed before the store completion call, not just by tick end. 143 Messaging tests, 94.27% line / 78.9% branch, gate live at 90%.
