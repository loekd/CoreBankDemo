# ADR-020: Instant rail cancels on budget exhaustion and answers 504 instead of 202

**Date:** 2026-09-08
**Status:** Accepted
**Deciders:** Architecture team
**Supersedes in part:** ADR-018's rule "a budget timeout is therefore never mapped to `504`" — that rule
held because a timed-out command could still execute later. This record adds the cancellation path
that makes the timeout unambiguous, and changes the answer only for the case that path proves.

> The implementation spec for this record named it ADR-019; that number was already taken by
> ADR-019 (generated Dev Proxy session config), so it is recorded here as ADR-020.

## Context

ADR-018 gave the instant rail a budget but no way to *stop*. When the budget ran out, PaymentsAPI
answered `202 Pending` and the background rail settled the payment later. That was honest — the
command might already have reached CoreBank — but it is not what an SCT Inst rail does. A real
instant rail gives a binary answer inside its budget: settled, or rejected. And "rejected" carries a
promise: nothing executed, so the payer may safely retry. The demo could not show that promise,
and could not simply swap the `202` for an error either, because the queued outbox row would still
be delivered by the background processor — a client retry would then be a second payment.

## Decision

Give the instant rail a cancellation path, and answer `504 Gateway Timeout` with `Status: Cancelled`
**only once the payment is provably dead**. The standard rail is untouched.

### Kernel: a fourth terminal status

- `MessageConstants.Status.Cancelled` is a terminal transport state on both command stores. It is
  written by exactly one path, `MarkAsCancelledAsync` (new on `IOutboxMessageStore<T>`,
  `IInboxMessageStore<T>` and `MessageRepositoryBase`), which mirrors `MarkAsCompletedAsync`: the
  `Status` concurrency token, one retry against reloaded values, `ProcessedAt` stamped from the
  `TimeProvider`, the reason recorded as `LastError`, and a no-op on any terminal row.
- Terminality is decided in one place. `IsTerminal` now includes `Cancelled`, and
  `MarkAsFailedWithRetryAsync`'s guards use it instead of `== Failed` — otherwise a late "release the
  claim" after a cancel would revive the row to `Pending` and the background rail would deliver a
  payment the caller was told never executed. Neither claim path ever picks a `Cancelled` row up
  again (the claim queries allow-list `Pending` and stale `Processing` only).
- One addition beyond the spec's letter: `MessageTransitionOutcome.Conflicted`. If the cancel's save
  conflicts, the reload decides: a terminal row reports `AlreadyTerminal`, anything else reports
  `Conflicted` — the cancel is never re-applied. The row may be `Processing` under someone else's
  claim (the boundary "never cancel a row that is `Processing`" outranks finishing the call's own
  intent), and even a row back at `Pending` has been through another writer's hands while the
  reload has discarded the caller's cached-payload mutation. Callers treat `Conflicted` exactly
  like `AlreadyTerminal`: not provably dead, so not `504`. Who may call `MarkAsCancelledAsync` is
  the caller's responsibility — a row it has just claimed, or one it has just inserted.

### CoreBankAPI: `POST /api/transactions/cancel`

The body is the original `TransactionRequest`, so a cancel that arrives before the original can
store a **tombstone** with the command's own columns (`Status = Cancelled`, `ProcessedAt`, a cached
`Cancelled` `TransactionResponse`), and `StoreIfNewAsync`'s unique key makes "cancel before original"
and "original before cancel" symmetric (AD-4). `TransactionCancellationHandler` then:

| CoreBank state | Action | Answer |
|---|---|---|
| No row | Store the tombstone | `200` `Cancelled` |
| `Pending` | `TryClaimByIdAsync` + `MarkAsCancelledAsync` (payload cached on the same save) | `200` `Cancelled` |
| `Pending`, claim lost | Re-read once, branch without claiming again | per the row's new state |
| `Completed` | Nothing — report the cached committed outcome | `200` `Completed`/`Failed` |
| `Cancelled` | Nothing — replay the cached cancellation | `200` `Cancelled` |
| `Processing` / `Failed` | Nothing | `409` with the current status |

A late-arriving original `process` for a tombstoned or cancelled id replays the cached `Cancelled`
payload through `TransactionIntakeHandler`'s existing "found an existing row" branch and never
executes — including with `X-Execute-Mode: inline`. A cancel never touches the ledger. It publishes
exactly one event, `com.corebank.transaction.cancelled`, for every cancellation CoreBank commits — see
the addendum below; the original text here read "never publishes an event". The checked-in OpenAPI
document owns the operation; the `409` body
is declared as `TransactionConflictResponse` — byte-identical on the wire to `TransactionResponse`,
but a separate schema so Kiota can generate it as the error type the client catches, without turning
the success model into an `ApiException`.

### PaymentsAPI: two-phase cancel inside the budget

`Payments:InstantRail` gains `CancelTimeoutMilliseconds` (default `1500`), validated so that
`AttemptTimeoutMilliseconds × MaxAttempts + CancelTimeoutMilliseconds ≤ BudgetMilliseconds`. The
budget is split: a **forward phase** of `Budget − CancelTimeout` (both the lock/turn wait and the
attempts stop there) and a **cancel allowance** of `CancelTimeout`, so a request thread is never
held beyond the budget.

```
forward phase ends without a committed outcome
  never forwarded (budget spent waiting, lock backend failed, status read failed)
      → TryClaimByIdAsync + MarkAsCancelledAsync locally   → 504 Cancelled
        claim lost → the row is in flight elsewhere        → 202 Pending
  attempts exhausted, still under the partition lock
      → CancelAsync(message) within CancelTimeout
          200 Cancelled            → MarkAsCancelledAsync  → 504 Cancelled
          200 Completed | Failed   → MarkAsCompletedAsync  → 200 with that outcome
          409 / timeout / error    → MarkAsFailedWithRetryAsync (release) → 202 Pending
```

The cancel of a claimed row happens while the request still holds the partition lock, so a later
row of the same partition and priority cannot overtake it. Every cancelled row carries a
`ProcessedAt` and a cached `Cancelled` `TransactionSubmission`/`TransactionResponse` payload, so a
duplicate key replays `504`/`Cancelled` with the original cancellation time, `GET
/api/transactions/{id}` serves it, and the ordering gate sees a terminal row. `ICoreBankApiClient`
gains `CancelTransactionAsync`; CoreBank's `409` becomes `CoreBankClientOutcome.Conflict` — a
distinct non-retry outcome carrying the reported status — never a `Retry` and never a `Success`.
The metric `corebankdemo.payment.instant.duration` gains `outcome=cancelled`.

**The residual `202`.** When neither a cancel nor a committed outcome can be established within
budget — the cancel timed out or failed on transport, or CoreBank answered `409` — the claim is
released to `Pending` exactly as before and the honest `202 Pending` remains; the background rail
settles the row later, exactly once. Two consequences follow. First, the release must be a
`MarkAsFailedWithRetryAsync`, never a cancel, because a delivery may be in flight at CoreBank.
Second, CoreBank may in fact have stored the cancellation this side never heard about, so the
background rail can later receive a replayed `200`/`Cancelled` for that row: `HttpForwardOutboxDeliveryStrategy.DeliverAsync`
then marks the outbox row `Cancelled` itself (the kernel's `MarkAsCompletedAsync` becomes an
`AlreadyTerminal` no-op), keeping the outbox and CoreBank's inbox in agreement. The inline path
handles the same replay the same way.

### Why `504`

After cancellation the timeout is no longer ambiguous: nothing executed, and the same idempotency
key replays the same answer, so a client retry with a new key is safe — the SCT Inst time-out
rejection. `504` says "the settlement did not answer in time"; the body's `Status: Cancelled`
carries the business meaning inside the frozen `PaymentResponse` shape. No field is added anywhere.

### Acceptance gate and tooling

- `LoadTestAssertionService`: `Cancelled` is terminal (never `NonTerminal`, never `Failed`), and
  `MessageStoreSummary`/`DrainResult` gain an additive `Cancelled` count. The gate becomes:
  `outbox.Total == outbox.Completed + outbox.Cancelled == k6 unique submitted`;
  `inbox.Completed == outbox.Completed`; `inbox.Cancelled ≤ outbox.Cancelled`; event stores hold
  `3 × outbox.Completed + inbox.Cancelled` (one `transaction.cancelled` per CoreBank-side
  cancellation — addendum below; a local cancel publishes nothing); balances conserved; FIFO
  within a priority class unchanged. With no cancellations this is the original N/N/3N/3N gate
  verbatim.
- `k6/script.js` admits `504` only with the wire word `Cancelled` on instant calls, sets
  `http.expectedStatuses(200, 202, 504)` per instant request so `http_req_failed` stays honest, and
  counts them in the informational `instant_cancelled` counter. The standard rail's checks are
  byte-identical. The setup probe still demands a `200 Completed`.
- DemoRunner: `PaymentOutcome.Cancelled` and `PaymentTrackingState.Cancelled`. A `504` is admitted
  on the instant rail only when its body says `Cancelled`; the standard rail still requires `202`. A
  cancelled row is proven by HTTP alone — never "Awaiting settlement", and a later
  `completed`/`failed` broadcast for it is a `Contradiction` (a `cancelled` broadcast confirms it —
  addendum below). In a burst, cancelled payments are tallied as `cancelled n`, never as failures
  and never as rejections; the HTTP leg's `504`s and the broadcast's withdrawals are summed there.

### Dev Proxy is not bypassed

The cancel travels through the same faulted hop as the payment leg (the existing `urlsToWatch`
pattern already matches `/api/transactions/cancel`). Under the "Instant-rail overrun" latency preset
the cancel is delayed too and typically ends as the residual `202` — the demo shows honestly that a
timeout on the cancellation channel is still an unknown. The `504` is demonstrated when CoreBank
never received or executed the command: a transport rejection, a validation failure, or
partition-lock starvation.

## Consequences

- The standard rail is provably untouched: no code path outside `scheme=instant` ever writes
  `Cancelled`, calls the cancel endpoint, or answers `504`; a standard-scheme resend of a cancelled
  key still replays the raw kernel status with `202` as before.
- `IOutboxMessageStore<T>`/`IInboxMessageStore<T>`, `ICoreBankApiClient`, `ICoreBankTransactionForwarder`
  and `TransactionsController`'s constructor gain members; all additive. Hand-written store fakes
  must implement `MarkAsCancelledAsync`.
- Persistent databases created before this change need no schema change: `Cancelled` is a new
  value in the existing `Status` column.
- Exactly-once still rests on the row-level claim with `Status` as the concurrency token; a cancel
  is just another claimed transition, so it can never race an execution into both winning.
- The cancel endpoint is unauthenticated, like every other endpoint in this demo: any caller can
  tombstone any not-yet-seen `TransactionId` with caller-supplied columns, and tombstones are never
  expired. Accepted — the demo has no authentication anywhere and this is not a new class of
  exposure (the same caller could submit the command itself) — but it is the reason a tombstone
  is terminal from birth and never executes, and it would need an owner check before any real
  deployment.

## Addendum (2026-09-08): `transaction.cancelled` — the residual `202` learns its outcome

**Supersedes in part** this record's own "a cancel never publishes an event" and the
`3 × outbox.Completed` event cardinality above.

**Problem observed live.** Under the Dev Proxy latency preset the cancel reached CoreBank, CoreBank
stored the cancellation, and its `200 Cancelled` reply arrived after the cancel allowance. PaymentsAPI
answered the residual `202 Pending`; the background rail later marked the row `Cancelled` from the
tombstone replay — and nobody was told. No event existed for a cancellation, so the DemoRunner row
waited forever and every `202` caller stayed blind.

**Decision.** CoreBank publishes `com.corebank.transaction.cancelled` for every cancellation it
commits — a tombstone stored before the original arrived, or a `Pending` row cancelled before
execution. The outbox row is enqueued in the same `SaveChanges` as the cancel (`StoreIfNewAsync` /
`MarkAsCancelledAsync`), and detached on any outcome other than a committed cancel, so an event exists
if and only if the cancel committed (AD-5). A replayed cancellation (`Cancelled`/`Completed` row found
by the cancel handler) publishes nothing — it was published once — and a local cancel that never left
PaymentsAPI publishes nothing: the caller holds the `504`. Payload
`TransactionCancelledEvent(TransactionId, Status: "Cancelled", ProcessedAt, Reason)` beside the three
frozen types, which stay byte-identical.

```
local cancel (never left PaymentsAPI) → 504 to caller, no event
CoreBank tombstone / pending cancel   → 200 Cancelled to PaymentsAPI AND transaction.cancelled event
replayed cancellation                 → 200 Cancelled, no new event
```

**PaymentsAPI** subscribes to it at `/events/transactions/cancelled` (fourth rule in both subscription
manifests) and records it through `RecordCommittedOutcomeAsync(id, Cancelled, processedAt)`: a
`Pending` payload becomes `Cancelled`; a cached `Completed`/`Failed`/`Cancelled` is never overwritten,
and a later `Completed`/`Failed` never overwrites a cached `Cancelled`. A duplicate submit then
replays `504 Cancelled` from the cached payload even while the row's transport status is still
`Pending` (or under the background rail's claim); the background delivery still ends with the row
`Cancelled` exactly once, from the replay, as before.

**DemoRunner** copies the wire string (ADR-015): a `transaction.cancelled` broadcast resolves an
`Awaiting`/`NotObserved`/`OutcomeUnknown` row to `Cancelled` with the event's `ProcessedAt` and the
note "withdrawn … safe to retry with a new key"; on a row already `Cancelled` by `504` it is a
confirmation, never a `Contradiction`; on a row HTTP proved `Completed`/`Failed` it is a
`Contradiction` ("HTTP proved X, broadcast says Cancelled"); the first broadcast still wins on a
`Settled`/`Rejected` row. In a burst it is tallied as `cancelled n` (never `Rejected`), drains the
proven leg, and is summed with the HTTP leg's `504`s rather than overwriting them; a `504`-retired id
is still ignored. The event prints as `Withdrawn — {id} · Reason: …` and is positive evidence, like
the `504` itself.

**Acceptance gate.** Event stores hold `3 × outbox.Completed + inbox.Cancelled`, all `Completed`. Any
other count fails: an inbox `Cancelled` row without its event means the event was lost or never
enqueued; an event without its row means a cancel that never committed was announced.

**Consequences.** `IOutboxEventEnqueuer` gains `EnqueueTransactionCancelledAsync` (returns the row so
a caller can detach it); `ITransactionEventIntakeHandler` gains an overload; `BusinessMetrics.MessageType`
gains `TransactionCancelled`; `TransactionCancellationHandler` now depends on the enqueuer and the
`CoreBankDbContext` — all additive. The `MessagingOutboxMessage` and payments `InboxMessage` schemas
are unchanged. The Dev Proxy configuration is untouched: the cancel still travels the faulted hop, so
under the latency preset the demo now shows the residual `202` resolving to `Cancelled` a moment
later instead of waiting forever.
