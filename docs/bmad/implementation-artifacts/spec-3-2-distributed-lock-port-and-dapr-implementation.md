---
title: 'Story 3.2: Distributed lock port and Dapr implementation'
type: 'feature'
created: '2026-08-22'
status: 'done'
baseline_commit: '57544ba89c381833b128911c37eba69a51399836'
review_loop_iteration: 1
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-3-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `CoreBankDemo.Messaging`'s `InboxProcessorBase`/`OutboxProcessorBase` (epic 2, already merged and tested — 153 passing tests) compile against and call `IDistributedLockService.ExecuteWithLockAsync(string, int, Func<CancellationToken,Task>, CancellationToken)` today. Breaking that signature silently would regress an already-accepted epic (FR-20; AD-6, AD-7).

**Approach:** `git rm` old `IDistributedLockService.cs`, `DaprDistributedLockService.cs`, `NoOpDistributedLockService.cs`. Rebuild `IDistributedLockService` with the **exact same external signature** Messaging already calls. Rebuild `DaprDistributedLockService`: hardcoded lock store `"lockstore"`, unique owner token, `daprClient.Lock`/`Unlock`, failed acquisition returns `false` never throws — but with the internal cancellation mechanism made independently unit-testable (extract the 5/6-of-lockExpirySeconds cooperative-cancellation math into a pure, injectable-`TimeProvider` function so it's provable without a real Dapr client or real elapsed time). Rebuild `NoOpDistributedLockService` (always returns false, no lock, no workload execution).

## Boundaries & Constraints

**Always:** `ExecuteWithLockAsync`'s public signature identical to what Messaging already calls (verified by building `CoreBankDemo.Messaging` unmodified against the new interface — it must still compile with zero changes); acquisition failure returns `false`, never throws; every exception inside the method is caught, logged, and turned into `false` — the method itself never throws; the 5/6-cooperative-cancellation math is unit-tested in isolation via injected `TimeProvider`, not by waiting on real timers; `DaprClient` reached only through this class (AD-6).

**Ask First:** None — the signature is fixed by the epic context, not a design choice this story makes.

**Never:** Change `ExecuteWithLockAsync`'s signature without also updating every Messaging call site in the same commit (not expected — do not do this unless the fixed signature proves genuinely impossible, which it isn't); add `LockRenewIntervalSeconds` or any renewal mechanism (AD-7).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Lock acquired, workload succeeds | Dapr lock call succeeds | Workload runs, lock released in `finally`, returns `true` | N/A |
| Lock not acquired | Dapr lock call reports failure | Workload never runs, returns `false` | no throw |
| Dapr client throws | Any exception from lock/unlock/workload | Logged, returns `false` | caught, never propagates |
| 5/6 cooperative cancellation | Elapsed time (via `TimeProvider`) passes 5/6 of `lockExpirySeconds` | The workload's token cancels; ambient token is untouched | pure function, no real clock wait in tests |
| Workload's own `OperationCanceledException` from the 5/6 token | Workload observes cancellation and throws | Caught distinctly, logged, returns `false` (not rethrown as a generic failure) | N/A |
| NoOp variant | Any call | Always returns `false` immediately, no Dapr call | N/A |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.ServiceDefaults/IDistributedLockService.cs`, `DaprDistributedLockService.cs`, `NoOpDistributedLockService.cs` — demolish and rebuild
- `CoreBankDemo.Messaging/InboxProcessorBase.cs`, `OutboxProcessorBase.cs` (epic 2, committed) — the existing, unmodified consumers this story must not break; used as a compile-compatibility check, not touched
- Epic context §Legacy Behavioral Reference — exact signature, lock store name, owner-token format, 5/6-lifetime cancellation behavior to preserve

## Tasks & Acceptance

**Execution:**
- [x] Rebuild the three files (edited in place rather than `git rm`+recreate — same demolish-and-rebuild intent, functionally identical, confirmed via `git diff --stat` that the pre-rebuild legacy shape is fully replaced)
- [x] Tests first: pure 5/6-cancellation-timing function (TimeProvider-driven, no real waits), then `IDistributedLockService`, `NoOpDistributedLockService`, `DaprDistributedLockService` (mocked `DaprClient` directly with Moq — confirmed via reflection on Dapr.Client 1.17.9 that `Lock`/`Unlock` are `abstract`/`virtual`, no wrapper seam needed)
- [x] Compile-compatibility check: after the rebuild, `dotnet build CoreBankDemo.Messaging/CoreBankDemo.Messaging.csproj` succeeds with zero source changes to Messaging

**Acceptance Criteria:**
- Given the rebuilt `IDistributedLockService`, when `CoreBankDemo.Messaging` builds against it, then zero Messaging source files require modification
- Given a Dapr lock acquisition failure, when `ExecuteWithLockAsync` is called, then it returns `false` without throwing
- Given elapsed time crossing 5/6 of `lockExpirySeconds` (simulated via `TimeProvider`), when the workload observes its token, then cancellation is signaled without waiting on a real clock in the test

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — expected: green, ServiceDefaults + Messaging both unaffected/passing
- `dotnet build CoreBankDemo.Messaging/CoreBankDemo.Messaging.csproj` — expected: green, no source changes needed

## Spec Change Log

- 2026-08-24 (step-04): implemented `IDistributedLockService`/`DaprDistributedLockService`/`NoOpDistributedLockService`/`CooperativeLockCancellation` per plan; `ExecuteWithLockAsync`'s signature unchanged, confirmed via `dotnet build CoreBankDemo.Messaging/CoreBankDemo.Messaging.csproj` (green, zero source changes) and `git diff --stat` against both `HEAD` and the baseline commit (empty). 68 new tests, all passing; 5/6-cooperative-cancellation math proven via `FakeTimeProvider.Advance` with zero real timer waits. Review (blind-hunter + edge-case-hunter + verification-gap, all model sonnet) found two convergent, confirmed real bugs, both patched: (1) the 5/6-cutoff timer callback calling `CancellationTokenSource.Cancel()` with no guard against a concurrent `Dispose()` — on a real `TimeProvider`, a workload finishing right as the cutoff fires could race an `ObjectDisposedException` onto an unobserved ThreadPool thread, crashing the process; fixed by extracting the callback into `CooperativeLockCancellation.CancelSafely`, which catches and swallows `ObjectDisposedException`, with a direct regression test disposing the token source first. (2) the cleanup `Unlock` call in `DaprDistributedLockService`'s `finally` block was passed the ambient `cancellationToken`, which could already be cancelled (e.g. shutdown mid-workload) and abort the RPC before it's attempted, leaking the Dapr lock until its own TTL expires; fixed by always releasing with `CancellationToken.None`, with a regression test cancelling the ambient token from inside the workload and asserting the token passed to `Unlock` is uncancellable. Verification-gap pass independently re-ran every claimed command/count and found zero discrepancies. Deferred (not fixed, tracked in `deferred-work.md`): ordinary ambient cancellation during lock *acquisition* logs at Error severity, same as a genuine Dapr failure, since it falls into the generic catch — a log-noise quality issue, not a correctness bug, out of scope for this patch; and a low-confidence note that `TryLockResponse` (which implements `IAsyncDisposable`) is read via `.Success` but never disposed, preserving the legacy explicit-Lock/explicit-Unlock shape rather than Dapr's newer `await using` sample pattern.
