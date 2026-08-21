---
title: 'Story 2.3: Claiming, retry, and poison state machine'
type: 'feature'
created: '2026-08-21'
status: 'done'
baseline_commit: 'fad0e774bee1f8fdbe54b4683478ee5c20a3dfd8'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-2-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Processors need identical batch-claiming and failure handling so retry semantics are the same in every store — the legacy inbox had no claim-to-Processing step and staleness was measured from creation time, not from when a message actually became stuck (FR-7; AD-3, AD-11; epic context AD-violation flags).

**Approach:** Extend `MessageRepositoryBase` (story 2.2) with claiming (`ClaimBatchForPartitionAsync`: select pending + stale-Processing rows for one partition, oldest-first, up to `BatchSize`, transition them to Processing atomically) and failure handling (`MarkAsFailedWithRetryAsync`: RetryCount+1, back to Pending, or terminal Failed at `MaxRetryCount` per AD-11 — transport-only Failed, business rejections are never routed through this method) plus `ExecuteInTransactionAsync` for atomic multi-row updates.

## Boundaries & Constraints

**Always:** Claiming and failure-marking are atomic (no message can be double-claimed under concurrent calls — proven on SQLite with concurrent claim attempts); only rows with `RetryCount < MaxRetryCount` are claimable; stale `Processing` (older than `ProcessingTimeout`) is reclaimed as claimable; batch size and ordering (oldest first by the store's timestamp) match `MessageConstants`.

**Ask First:** Any change to what counts as "oldest" if a store has ambiguous timestamps.

**Never:** Processor poll loops, locking, or delivery dispatch (2.4/2.5); conflate this with AD-11 business-rejection completion (that path never calls `MarkAsFailedWithRetryAsync` — it stores a Completed row with a failure payload, which stories 4.x implement).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Claim batch | N pending rows in one partition, N > BatchSize | Exactly BatchSize claimed, oldest first, now Processing | N/A |
| Claim excludes poisoned | Rows with RetryCount == MaxRetryCount | Never claimed | N/A |
| Claim excludes other partitions | Rows in partition 2 when claiming partition 0 | Not claimed | N/A |
| Stale reclaim | Processing row older than ProcessingTimeout | Claimable again | N/A |
| Fresh Processing not reclaimed | Processing row younger than ProcessingTimeout | Not claimable | N/A |
| Concurrent claim | Two callers claim the same partition simultaneously | No row claimed by both | N/A |
| Retry under limit | RetryCount = 2, MaxRetryCount = 5 | Back to Pending, RetryCount = 3, LastError set | N/A |
| Retry at limit | RetryCount = 4 (about to become 5 = MaxRetryCount) | Terminal Failed, RetryCount = 5, LastError set | N/A |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.Messaging/MessageRepositoryBase.cs` (story 2.2, committed fad0e77) — extend, don't replace `StoreIfNewAsync`
- `tests/CoreBankDemo.Messaging.Tests/IdempotentStoreTestSupport.cs` — reuse `SqliteMessagingTestBase`, test entities; extend with `TimeProvider`-injectable fixture (use `FakeTimeProvider` or manual `TimeProvider` stub — no `DateTime.Now` per constraints.md)
- Epic context Legacy Behavioral Reference — old `GetPendingMessageIdsForPartitionAsync`/`MarkAsFailedWithRetryAsync`/`ExecuteInTransactionAsync` semantics (copy shape, fix the staleness-basis and no-claim-step violations)

## Tasks & Acceptance

**Execution:**
- [x] Tests first covering the full I/O matrix (SQLite, injected `TimeProvider` for staleness control)
- [x] `ClaimBatchForPartitionAsync(partitionId, batchSize)` — atomic select+transition (single `SaveChangesAsync` per claimed set via a `Status`-concurrency-token retry loop; concurrency proven via concurrent-task test using two contexts). Uses the repository's constructor-injected `TimeProvider` (no separate per-call parameter — consistent with `StoreIfNewAsync` and every other repository method).
- [x] `MarkAsFailedWithRetryAsync(message, error)` — RetryCount+1, Pending or terminal Failed at MaxRetryCount, LastError set
- [x] `ExecuteInTransactionAsync(Func<Task>)` — wraps an operation in a DB transaction, tested for atomicity (partial failure leaves no partial state)

**Acceptance Criteria:**
- [x] Given concurrent claim attempts on one partition, when both run, then the claimed sets are disjoint
- [x] Given a message at RetryCount = MaxRetryCount-1, when it fails, then it becomes terminal Failed, not Pending
- [x] Given `ExecuteInTransactionAsync` wrapping an operation that throws partway, when it throws, then no partial row change persists

**Implementation note (staleness basis, frozen Problem statement):** the legacy
violation was staleness measured from creation/receipt time rather than from
when a message actually became stuck. `IMessage`/`IInboxMessage`/`IOutboxMessage`
are frozen by story 2.1's pinned `MessageContractsTests` (exact member-count
assertions) and could not gain a new "claimed at" field. Since
`IInboxMessage.ReceivedAt` / `IOutboxMessage.CreatedAt` are already documented
(story 2.1) as doing double duty as "ordering timestamp for claims", the fix
implemented here is: `ClaimBatchForPartitionAsync` stamps that same timestamp
forward to the claim instant, but ONLY for rows that were already `Processing`
before the call — i.e. a true stale-claim reclaim — so a subsequent
stale-reclaim check measures from the claim, not the original receipt/creation.
Rows claimed fresh from `Pending` are never stamped, so a row's true arrival
order is preserved both on its very first claim and if it is later reclaimed
after going stale — first-claim FIFO ordering by that timestamp is unaffected
across separate claim calls, not just within a single one. (An earlier
implementation stamped every claimed row unconditionally, including fresh
`Pending` claims — this destroyed a row's true arrival timestamp on its first
claim and, if the row was later reclaimed after crashing, permanently lost its
place in the arrival-order queue relative to messages that arrived later,
silently violating AD-4's oldest-first FIFO guarantee across separate claim
calls; fixed by capturing each message's pre-claim status before mutating it
and only stamping forward when that pre-claim status was `Processing`.)
Covered by `Reclaimed_stale_row_is_not_immediately_reclaimable_again` (inbox),
`Reclaimed_stale_row_preserves_original_arrival_order_relative_to_a_later_arrival`
(inbox, the cross-claim FIFO-preservation proof), and the outbox mirror in
`ClaimBatchForPartitionAsyncOutboxTests`.

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — expected: green, Messaging ≥90%

## Spec Change Log

- 2026-08-21 (step-04): review caught a real ordering-invariant bug: ClaimBatchForPartitionAsync stamped the ordering timestamp forward on EVERY claim including fresh Pending ones, permanently corrupting FIFO order for any message that later crashed and was reclaimed (violates AD-4 per-partition ordering across separate claim calls). Fixed: only reclaimed (pre-claim status == Processing) rows get their timestamp stamped; captured pre-claim status once before the concurrency-retry loop mutates Status. Regression test added proving A (claimed, crashed, reclaimed) still sorts before B (arrived later, still Pending). Also: MarkAsFailedWithRetryAsync now handles DbUpdateConcurrencyException, attaches detached entities, and no-ops on already-Failed messages; deterministic ThenBy(Id) tie-break added; partitionId validated; unguarded Entries cast replaced with pattern match. 96 Messaging tests, 93.03% line / 84.61% branch, gate live at 90%.
