# Epic 2 (E1) Retrospective — Messaging Kernel

**Date:** 2026-08-22 · **Stories:** 2.1–2.6 (all done) · **Commits:** 23cdc35, f9a8945, fad0e77, 691f246, 854ba73, aa32951, + 2.6 close

## Verdict: ACCEPTED

`CoreBankDemo.Messaging` is rebuilt from scratch: 153 tests, 94.54% line / 78.9% branch coverage on the kernel, the 90% gate live throughout. Both processor bases (`OutboxProcessorBase`, `InboxProcessorBase`) share one poll/lock/claim/dispatch loop shape — the single-kernel invariant (AD-3) that was the epic's whole reason to exist, since the legacy `MessagingOutboxProcessor` bypassing the shared base was the defect (ADR A2) this epic was built to eliminate.

## Evidence

- FNV-1a partition math pinned to 12 legacy-executed known vectors (story 2.1) — ordering identity compatible with `main`.
- Race-safe `StoreIfNewAsync` via provider-aware unique-violation detection, SQLite + Postgres (story 2.2).
- Atomic batch claiming, retry-to-terminal-Failed at MaxRetryCount, `ExecuteInTransactionAsync` (story 2.3).
- `IOutboxDeliveryStrategy`/`IInboxMessageHandler` ports — delivery/handling classification lives in the kernel, never the strategy/handler, per AD-11 (stories 2.4/2.5).
- Failure-path hardening: lock-service exceptions (sync and async), all-partitions-unavailable, lock-supplied cancellation tokens (story 2.6).

## Real bugs found by review and fixed — the process working as designed

1. **FIFO-ordering corruption (2.3):** `ClaimBatchForPartitionAsync` stamped the ordering timestamp on every claim, not just reclaims — a message that crashed mid-processing and was later reclaimed permanently lost its place in the per-partition arrival-order queue, violating AD-4. Fixed with a pre-claim-status capture; regression test proves arrival order survives a claim-crash-reclaim cycle.
2. **Duplicate-delivery risk (2.4):** a completion-persistence failure *after* successful delivery was misclassified as a delivery failure, routing an already-delivered message back through the retry path — violating AD-11's exactly-once contract. Fixed by separating delivery-failure and completion-failure into distinct try/catch scopes; the fix was correctly mirrored (not regressed) into `InboxProcessorBase` in story 2.5.
3. **Partition fan-out abort on synchronous throw (2.6):** the eager `Select/ToArray` fan-out let a lock service that threw *synchronously* (before returning any `Task`) abort the whole enumeration, silently skipping every partition after the throwing one. Fixed by making the per-partition lock call `async` with its own try/catch — then review caught the fix over-logging ordinary cancellation as a lock-service failure, split into a dedicated cancellation catch.
4. **Cross-story consistency gap (2.5):** `MarkAsFailedWithRetryAsync` was unguarded in both processors' failure catch — a store hiccup while recording a failure could abort the rest of a partition's batch. Caught once in 2.5's review, backported to the already-committed `OutboxProcessorBase` for consistency rather than leaving the asymmetry.

## Process notes

- Three real, load-bearing bugs (not stylistic nits) surfaced across six stories — the three-layer review panel (blind hunter, edge-case hunter, verification-gap) earns its cost precisely on kernel code where every downstream processor inherits the defect silently.
- Backporting a fix found in a later story to an earlier already-committed sibling (2.5→2.4) kept the two processors from drifting into inconsistent failure semantics — worth doing whenever the same pattern is deliberately mirrored.
- One review finding was a scope-honesty correction, not a code bug: a claim that "lock expiry mid-batch" was covered when only the cancellation-token *seam* was proven (the real timing mechanism is epic 3's). Corrected in the spec rather than left to imply more coverage than exists.

## Carry-forward obligations

- Epic 3 delivers the real `IDistributedLockService`/`DaprDistributedLockService` with 5/6-lock-lifetime cooperative cancellation — story 2.6 proved the kernel honors whatever token it's given; the real timing behavior is untested until then.
- `CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxProcessor.cs` and both APIs' legacy inbox/outbox processors still exist, un-migrated, outside `CoreBankDemo.Rebuild.slnf` — epics 4/5 replace them with concrete strategies/handlers on this kernel.
- `queue_duration_ms` activity tag understates latency for reclaimed messages (computed from the ordering timestamp, which story 2.3 established gets reset only on true reclaims — but a reclaimed message's *first* claim-to-reclaim window isn't reflected). Known limitation, not fixed — flagged for epic 7 docs if it matters for the demo narrative.
