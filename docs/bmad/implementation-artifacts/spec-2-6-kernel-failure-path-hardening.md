---
title: 'Story 2.6: Kernel failure-path hardening'
type: 'feature'
created: '2026-08-22'
status: 'done'
baseline_commit: 'aa32951639c962cd5d6c914f2f38396480c974a0'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-2-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The kernel's guarantees (no message lost, none double-completed, next tick always proceeds) are proven story-by-story on the happy paths; the epic's own exit bar demands the ugly paths be tests, not claims — several are still unexercised: lock-not-acquired across a full tick, lock-service exceptions (not just "returns false"), cancellation during the lock-acquisition call itself (vs. mid-dispatch, already covered), and the epic-context requirement that 5/6-lock-lifetime cooperative cancellation be honored once the real handle exists (currently only the ambient `CancellationToken` is honored — no timing assertion exists anywhere in the kernel).

**Approach:** Close the specific gaps the epic context and stories 2.1–2.5 left open, without re-testing what's already proven: (1) `IDistributedLockService.ExecuteWithLockAsync` throwing (not just returning false) at both the Inbox and Outbox processors — tick must survive; (2) a full tick where every partition fails to acquire its lock — zero work happens, no throw; (3) an explicit assertion, using a fake lock service that supplies its own cancellation token, that the processors' dispatch loop stops within that token's cancellation rather than continuing to claim/dispatch — the seam epic 3 will drive with real 5/6-lifetime timing; (4) a final kernel-wide coverage gate check to confirm ≥90% line holds with these tests added.

## Boundaries & Constraints

**Always:** New tests only extend `OutboxProcessorBaseTests.cs`/`InboxProcessorBaseTests.cs` (or a new `KernelFailurePathTests.cs` if that reads clearer) — no production code changes unless a test reveals a genuine gap; every new test names the specific guarantee it proves (no message lost / no double-completion / tick survives).

**Ask First:** If a test reveals a real bug (as 2.3/2.4 did) — fix it and document, same pattern as prior stories.

**Never:** Re-derive coverage already proven in 2.1–2.5 (partition math, claim/retry state machine, StoreIfNewAsync races, completion-vs-failure separation); add new kernel abstractions — this story only hardens what exists.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Lock service throws (Outbox) | `ExecuteWithLockAsync` throws for one partition | That partition's exception logged, other partitions still process, tick doesn't crash | caught at partition level |
| Lock service throws (Inbox) | Same, inbox side | Mirrors outbox | caught at partition level |
| All locks unavailable | Every partition returns false | Tick completes with zero dispatches, no throw, next tick still scheduled | N/A |
| Cancellation via lock-supplied token | Fake lock service passes an already-cancelling token into the workload | Dispatch loop stops promptly, no message left double-claimed or lost | N/A |
| Kernel coverage | Full Messaging suite after additions | ≥90% line, gate passes | N/A |

</frozen-after-approval>

## Code Map

- `tests/CoreBankDemo.Messaging.Tests/OutboxProcessorBaseTests.cs`, `InboxProcessorBaseTests.cs` (stories 2.4/2.5, committed 854ba73/aa32951) — extend with the new scenarios; reuse existing fakes/mocks (`FakeServiceScope`/`FakeServiceScopeFactory`, lock-service mocks) rather than inventing new ones
- `CoreBankDemo.Messaging/{Outbox,Inbox}ProcessorBase.cs` — `ProcessPartitionsAsync`/`ProcessPartitionAsync` are the code under test; no changes expected unless a gap is found
- Epic context §"Requirements & Constraints": "Failure paths are proven by tests, not claims: lock acquisition failure, lock expiry mid-batch, delivery timeout, repository exception, cancellation during dispatch — no message lost or double-completed, next tick always proceeds" — this story is the epic's own acceptance bar, closing the subset not yet covered by 2.1–2.5

## Tasks & Acceptance

**Execution:**
- [x] Lock-service-throws tests for both processors (partition-level isolation, tick survives)
- [x] All-partitions-lock-unavailable test for both processors (zero dispatch, no throw)
- [x] Lock-supplied-cancellation-token test for both processors (dispatch loop honors it promptly)
- [x] Full `dotnet test CoreBankDemo.Rebuild.slnf` run confirming ≥90% line coverage holds

**Acceptance Criteria:**
- Given a lock service that throws for one partition among four, when a tick runs, then the other three partitions still process normally and the exception is logged, not rethrown
- Given a lock service that never grants any partition its lock, when a full tick runs, then it completes with no dispatch and no exception
- Given a cancellation token supplied by the lock workload's own token (not the outer `stoppingToken`), when it fires mid-partition, then the dispatch loop stops without losing or double-completing the in-flight message

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — expected: green, Messaging ≥90% line, epic-2 exit bar satisfied for the scenarios this story covers (see scope note below)

**Scope note — epic-2 exit bar honesty:** The epic-context exit bar lists five failure paths: lock acquisition failure, lock expiry mid-batch, delivery timeout, repository exception, cancellation during dispatch. This story closes all of them for the seam the kernel controls, but "lock expiry mid-batch" specifically is **not** exercised via real or simulated elapsed-time expiry — no `DaprDistributedLockService` exists yet (epic 3's deliverable), so there is no 5/6-lock-lifetime timer to expire. The "Cancellation via lock-supplied token" tests instead cancel a fake lock service's own `CancellationTokenSource` directly from test code. What that proves is narrower and deliberately so: the kernel's dispatch loop honors whatever cancellation token the lock service hands it (the seam), stopping promptly without losing or double-completing the in-flight message — not the expiry-*timing* mechanism itself, which does not exist yet to test. Real 5/6-lock-lifetime timing behavior is deferred to epic 3 alongside the real lock service. This does not change this story's own scope or its "done" status — the Intent section's actual deliverables (lock-throws isolation, all-locks-unavailable, lock-supplied-cancellation-token honored) are genuinely complete — it only corrects how far that completion reaches toward the epic-2 exit bar's "lock expiry mid-batch" item specifically.

## Spec Change Log

- 2026-08-22: Implemented. Extended `OutboxProcessorBaseTests.cs`/`InboxProcessorBaseTests.cs` (mirrored) with: (1) a lock-service-throws-asynchronously test per processor proving the other three of four partitions still process and the exception is logged, not rethrown; (2) a lock-service-throws-synchronously hardening variant per processor (a fake `IDistributedLockService` whose `ExecuteWithLockAsync` throws before returning any `Task`, e.g. mimicking eager argument validation); (3) a full-tick all-partitions-lock-unavailable test per processor at `PartitionCount = 4` (existing coverage was single-partition only); (4) a lock-supplied-cancellation-token test per processor using a fake lock service that hands the workload a `CancellationToken` it owns itself (mirroring `DaprDistributedLockService`'s 5/6-lock-lifetime `workCts`), distinct from the ambient `stoppingToken`, proving the dispatch loop stops promptly on that token specifically and the ambient token is never touched.
- **Genuine bug found and fixed**: the synchronous-throw test failed against the pre-existing code. `ProcessPartitionsAsync`'s fan-out built its per-partition tasks via an eager `Enumerable.Range(...).Select(...).ToArray()`; `ProcessPartitionUnderLockAsync` was a non-`async` method that directly returned the `Task` from `_lockService.ExecuteWithLockAsync(...)`. A lock-service implementation that threw *synchronously* (before returning any `Task`) aborted the `ToArray()` enumeration itself, so every partition after the throwing one was silently never even attempted — violating the "other partitions still process" guarantee for a subset of realistic lock-service implementations (e.g. ones with synchronous argument validation). Fixed in both `OutboxProcessorBase.cs` and `InboxProcessorBase.cs` by making `ProcessPartitionUnderLockAsync` `async` with its own `try`/`catch(Exception)` around the `ExecuteWithLockAsync` call, logging at `LogLevel.Error` and continuing — this both guarantees every partition is always attempted regardless of an earlier one's failure mode, and satisfies the I/O matrix's "caught at partition level" requirement (previously the swallow only happened at the tick level, in `RunTickAsync`).
- Final state: 151 Messaging tests (up from 133), all green; `CoreBankDemo.Messaging` line coverage 92.89% / branch 78.9% / method 100% — gate (≥90% line) holds. Full `dotnet test CoreBankDemo.Rebuild.slnf` run green across all four test projects, exit code 0.
- Nothing left incomplete. No new kernel abstractions were added; existing fakes (`AlwaysAcquiringLockService`, `NeverAcquiringLockService`, `FakeServiceScopeFactory`) were reused, with two new small fakes added (`SelectivelyThrowingLockService`, `LockSuppliedCancellationLockService`) local to each test file, matching the existing per-file fake convention rather than introducing a shared test-infra file.

## Spec Change Log

- 2026-08-22 (step-04): the synchronous-throw hardening test found a real bug: ProcessPartitionsAsync's eager Select/ToArray fan-out let a lock service that threw SYNCHRONOUSLY (before returning any Task) abort the whole enumeration, silently skipping every partition after the throwing one. Fixed: ProcessPartitionUnderLockAsync made async with its own try/catch. Review then caught the fix over-broadly logging OperationCanceledException as "Lock service failed" — split into a dedicated (unconditional, deliberately not gated on the ambient token — see rationale in commit) catch (OperationCanceledException) { throw; } before the generic catch. Also corrected an overclaim: "lock expiry mid-batch" real elapsed-time timing is deferred to epic 3 (no DaprDistributedLockService exists yet); this story proves only that the kernel honors whatever cancellation token the lock service supplies. 153 Messaging tests, 94.54% line / 78.9% branch, gate live at 90%.
