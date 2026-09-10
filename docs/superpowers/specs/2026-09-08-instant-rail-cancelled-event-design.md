# Instant rail: broadcast a transaction.cancelled event so a residual 202 resolves

> **Status:** Implemented
> **Kind:** story spec
> **Original date:** 2026-09-08
> **Migrated from:** `docs/bmad/implementation-artifacts/spec-instant-rail-cancelled-event.md` on 2026-09-10
> **Related:** [ADR-020](../../adr/ADR-020-instant-rail-timeout-cancellation.md); [ADR-015](../../adr/ADR-015-presentation-safe-terminal-demo-console.md)



## Intent

**Problem:** When the cancel reaches CoreBank but its answer arrives after the cancel allowance, PaymentsAPI answers the residual `202 Pending` and the background rail later marks the row `Cancelled` from the tombstone replay. Nobody is told: no event is published for a cancellation, so the DemoRunner row waits forever and every `202` caller stays blind (observed live: transaction `fae179d5…` under the Dev Proxy latency preset).

**Approach:** CoreBank publishes a `com.corebank.transaction.cancelled` CloudEvent for every cancellation it commits (tombstone or pending-row cancel), enqueued atomically with the cancel in its messaging outbox (AD-5). PaymentsAPI's event inbox records it as the committed outcome, the DemoRunner resolves a waiting row to `Cancelled` and treats it as confirmation of a row already proven by `504`, and the load-test gate counts one extra event per CoreBank-side cancellation. A local cancel (command never left PaymentsAPI) emits nothing: the caller already holds the `504`.

## Boundaries & Constraints

**Always:** Enqueue the event in the same `SaveChanges`/transaction as the cancelled inbox row; never an event without a committed cancel, never a committed cancel without its event. Define the type and payload once in ServiceDefaults `CloudEventTypes` (AD-12) and copy the wire string into the DemoRunner (ADR-015). Keep `Cancelled` cached outcomes immutable: the event never overwrites a committed `Completed`/`Failed`/`Cancelled` payload, and a later `Completed`/`Failed` never overwrites a cached `Cancelled`. Keep the three existing event types byte-identical. Coverage ≥90 % per project; TDD.

**Ask First:** Any new column on `MessagingOutboxMessage` or the payments `InboxMessage`; changing the topic, pub/sub name or subscription scopes; any Dev Proxy configuration change.

**Never:** Publish from the local-cancel path or from a replayed cancellation (`Cancelled`/`Completed` rows found by the cancel handler publish nothing). Touch the ledger. Let the DemoRunner treat a `Cancelled` broadcast as a contradiction of a `504` row, or let it move a `Settled`/`Rejected` row. Change the standard rail.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Tombstone | Cancel for an id CoreBank never received | Tombstone row + `transaction.cancelled` outbox row in one save; event published with `Status: Cancelled`, `ProcessedAt`, `Reason` | Unique-key loss → no event row is left tracked; resolve against the winner as today |
| Pending row cancelled | `TryClaimByIdAsync` + `MarkAsCancelledAsync` = `Applied` | Event row committed in the same save | `AlreadyTerminal`/`Conflicted` → no event row persists |
| Replayed cancel | Row already `Cancelled`/`Completed` | `200` as today, no new event | N/A |
| Event reaches PaymentsAPI | Outbox row `Pending` with `Pending` payload (residual `202`) | Payload becomes `Cancelled`; duplicate key replays `504 Cancelled`; background delivery still ends with the row `Cancelled` exactly once | Concurrency conflict propagates → kernel retries the event |
| Event after the rail already marked `Cancelled` | Payload already `Cancelled` | No-op, logged at debug/none | N/A |
| Duplicate event | Same `(TransactionId, EventType, "")` | Deduped by the inbox unique index, acked | N/A |
| DemoRunner, row `Awaiting`/`NotObserved`/`OutcomeUnknown` (202) | `transaction.cancelled` arrives | Row → `Cancelled`, `BroadcastOutcome = Cancelled`, `ProcessedAt` from the event, note "withdrawn … safe to retry with a new key" | N/A |
| DemoRunner, row already `Cancelled` by `504` | Same event | Row stays `Cancelled`, `BroadcastOutcome = Cancelled`, no `Contradiction` | N/A |
| DemoRunner, row `Settled`/`Rejected` or HTTP `Completed`/`Failed` | Same event | `Contradiction` note "HTTP proved X, broadcast says Cancelled" (first broadcast wins as today) | N/A |
| DemoRunner burst | Event for an unresolved burst transaction (accepted via `202`) | Tally `cancelled n` on the burst, never `Rejected`; a `504`-retired id is ignored as today | N/A |
| Load-test gate | Run with CoreBank-side cancellations | Event stores hold `3 × outbox.Completed + inbox.Cancelled`, all `Completed`; other checks unchanged | Any other count fails the gate |


## Code Map

- `CoreBankDemo.ServiceDefaults/CloudEventTypes/Constants.cs:5-7` -- add `TransactionCancelled = "com.corebank.transaction.cancelled"`; new `TransactionCancelledEvent(TransactionId, Status, ProcessedAt, string? Reason)` beside `TransactionFailedEvent.cs`. Tests: `tests/CoreBankDemo.ServiceDefaults.Tests/CloudEventTypes/ConstantsTests.cs`, `CloudEventJsonSnapshotTests.cs` (exact camelCase JSON, nulls emitted). `BusinessMetrics.cs:163-170` `MessageType.TransactionCancelled` (+ `BusinessMetricsTests`).
- `CoreBankDemo.CoreBankAPI/Outbox/OutboxEventEnqueuer.cs:11-13` -- `EnqueueTransactionCancelledAsync(InboxMessage, string reason, ct)` returning the added `MessagingOutboxMessage` (so a caller can detach it); columns as `EnqueueTransactionFailedAsync` with `TransactionStatus = Cancelled`, `ErrorReason = reason`, `EventOccurredAt = ProcessedAt`. `DaprOutboxDeliveryStrategy.cs:14-35` -- add the switch arm (unknown types still throw).
- `CoreBankDemo.CoreBankAPI/Inbox/TransactionCancellationHandler.cs` -- inject `CoreBankDbContext` + `IOutboxEventEnqueuer` (`Program.cs:56-58`). Tombstone `:106-137`: enqueue before `StoreIfNewAsync` so its single save commits both; detach the event row when `stored == false` or the save throws. Pending `:214-261`: enqueue before `MarkAsCancelledAsync`; detach unless `Applied`. Update the class doc `:52-61`. Tests: `tests/CoreBankDemo.CoreBankAPI.Tests/TransactionCancellationHandlerTests.cs`, persistence `tests/CoreBankDemo.Persistence.IntegrationTests/CoreBankApi/*` (atomicity on real PostgreSQL: duplicate tombstone leaves zero event rows).
- `dapr/components/subscription-transaction-events.yaml:9-16` and `dapr/components-loadtest/…` -- fourth rule → `/events/transactions/cancelled`. `CoreBankDemo.PaymentsAPI/Controllers/TransactionEventsController.cs:29-55` new action; `Handlers/TransactionEventIntakeHandler.cs:49-71,155-161` overload with the transaction-wide sentinel; `Handlers/TransactionEventHandler.cs:49-70` case → `RecordCommittedOutcomeAsync(id, Cancelled, processedAt)` (`OutboxRepository.cs:80-101` already refuses to overwrite). Tests: `TransactionEventHandlerTests.cs:236-257` theory, `TransactionEventsControllerTests.cs`, `TransactionEventIntakeHandlerTests.cs`, `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/TransactionEventIntakeWiringTests.cs:55-232` (route table + both YAML manifests).
- `CoreBankDemo.DemoRunner/Application/Ports/IOutcomeFeed.cs:12-73` -- `OutcomeEventTypes.TransactionCancelled`, `TransactionCancelledWireEvent`, fourth slot + `From` factory, `ProcessedAt`/`IsTerminal` include it. `Infrastructure/DaprOutcomeFeed.cs:318-343` parse arm. `Application/OperatorConsoleController.cs`: `ResolveTrackedPayment:1952-1984` cancelled branch per matrix; `CountForBurst:1990-2008` third bucket into `BurstProgress.CancelledPayments` — and the HTTP loop `:930-943` must add its own count to the broadcast tally instead of overwriting it; `EventSummary:2261` (`Withdrawn — {id} · Reason: …`), `EventDetail:2294` (Status + Reason), `:1893` evidence positivity. Tests: `tests/CoreBankDemo.DemoRunner.Tests/Infrastructure/DaprOutcomeFeedTests.cs`, `Application/OperatorConsoleControllerTests.cs:624-654,1402-1450`, `Fakes/OperatorHarness.cs:512-589` (`PushCancelled`).
- `CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs:512-544` -- `3 × completed + coreBankInbox.Cancelled` for both event stores; detail string. Tests `tests/CoreBankDemo.LoadTestSupport.Tests/LoadTestAssertionServiceTests.cs`.
- Docs: ADR-020 addendum (`:67-68,:126,:132-135`), `ARCHITECTURE.md:679`, `README.md:59-60,:305`, `CoreBankDemo.LoadTests/README.md:41`, `.claude/commands/run-load-tests.md:21`, `spec-instant-rail-timeout-cancel.md` gets a one-line "superseded in part" note only.

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.ServiceDefaults/*` -- type constant, payload record, metric member; tests -- contract owner first.
- [x] `CoreBankDemo.CoreBankAPI/*` -- enqueuer method, delivery arm, atomic enqueue in both cancel paths; unit + persistence tests for the matrix rows.
- [x] `dapr/components*/subscription-transaction-events.yaml`, `CoreBankDemo.PaymentsAPI/*` -- route, action, intake overload, handler case; tests incl. wiring test.
- [x] `CoreBankDemo.DemoRunner/*` -- wire record, parse, resolve/burst/summary; tests for every DemoRunner matrix row.
- [x] `CoreBankDemo.LoadTestSupport/*` -- cardinality; tests.
- [x] Docs listed above.

**Acceptance Criteria:**
- Given an instant payment that ended as a residual `202` and was cancelled by CoreBank's tombstone, when the event is delivered, then a duplicate submit replays `504 Cancelled` and a tracking DemoRunner row reads `Cancelled` without operator action.
- Given a local cancel, when the payment is inspected afterwards, then no `transaction.cancelled` row exists in either event store.
- Given a mixed run with cancellations, when drained, then `get_assertion_results` passes.

## Design Notes

**Why atomic with the cancel, not with the reply.** The cancel handler's reply to PaymentsAPI can be lost (that is this bug); the event is the durable channel, so it must exist iff the cancel committed. Both cancel paths already end in exactly one `SaveChanges` (`StoreIfNewAsync`, `MarkAsCancelledAsync`); pre-adding the outbox row to the same `DbContext` makes it part of that save, and detaching it on any non-applied outcome keeps the context clean for the next save (mirrors `StoreIfNewAsync`'s own detach-on-failure discipline).

**Kinds of cancel and who is told:**
```
local cancel (never left PaymentsAPI) → 504 to caller, no event
CoreBank tombstone / pending cancel   → 200 Cancelled to PaymentsAPI AND transaction.cancelled event
replayed cancellation                 → 200 Cancelled, no new event (already published once)
```

## Verification

**Commands:**
- `dotnet build CoreBankDemo.sln` -- expected: clean.
- `dotnet test CoreBankDemo.UnitTests.slnf` -- expected: green, ≥90 % line per project.
- `dotnet test CoreBankDemo.IntegrationTests.slnf` -- expected: green (Docker).
- `load-test` skill run under the latency preset -- expected: gate passes with `Cancelled` rows and the extra events counted.

## Suggested Review Order

**Entry point — the cancel and its event commit together**

- Tombstone: event row pre-added so `StoreIfNewAsync`'s one save commits both; detached on a lost race
  [`TransactionCancellationHandler.cs:138`](../../../CoreBankDemo.CoreBankAPI/Inbox/TransactionCancellationHandler.cs#L138)
- Pending row: `ProcessedAt` pre-stamped, event enqueued, kept only when the cancel `Applied`
  [`TransactionCancellationHandler.cs:281`](../../../CoreBankDemo.CoreBankAPI/Inbox/TransactionCancellationHandler.cs#L281)
- Detach keeps the scoped context usable after any non-committed outcome
  [`TransactionCancellationHandler.cs:328`](../../../CoreBankDemo.CoreBankAPI/Inbox/TransactionCancellationHandler.cs#L328)
- Review fix: the kernel keeps a pre-stamped `ProcessedAt`, so row, payload and event agree under a real clock
  [`MessageRepositoryBase.cs:578`](../../../CoreBankDemo.Messaging/MessageRepositoryBase.cs#L578)
- Decision record: why the event is the durable channel when the cancel reply is lost
  [`ADR-020:171`](../../adr/ADR-020-instant-rail-timeout-cancellation.md#L171)

**Contract owner — the fourth event type**

- Type id beside the three frozen ones (AD-12)
  [`Constants.cs:16`](../../../CoreBankDemo.ServiceDefaults/CloudEventTypes/Constants.cs#L16)
- Payload mirrors `TransactionFailedEvent` with `Reason` in place of `ErrorReason`
  [`TransactionCancelledEvent.cs:7`](../../../CoreBankDemo.ServiceDefaults/CloudEventTypes/TransactionCancelledEvent.cs#L7)
- Enqueuer returns the row so the caller can detach it
  [`OutboxEventEnqueuer.cs:88`](../../../CoreBankDemo.CoreBankAPI/Outbox/OutboxEventEnqueuer.cs#L88)
- Delivery switch arm; unknown types still throw
  [`DaprOutboxDeliveryStrategy.cs:25`](../../../CoreBankDemo.CoreBankAPI/Outbox/DaprOutboxDeliveryStrategy.cs#L25)

**PaymentsAPI — the residual 202 learns its outcome**

- Cancelled event recorded as the committed outcome; a cached terminal payload is never overwritten
  [`TransactionEventHandler.cs:60`](../../../CoreBankDemo.PaymentsAPI/Handlers/TransactionEventHandler.cs#L60)
- Review fix: a cancelled event must say `Cancelled`, otherwise the kernel retries/poisons it
  [`TransactionEventHandler.cs:146`](../../../CoreBankDemo.PaymentsAPI/Handlers/TransactionEventHandler.cs#L146)
- Duplicate submit replays `504` from the cached payload even while the row is still `Pending`
  [`PaymentsController.cs:168`](../../../CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs#L168)
- Cancelled is immutable in both directions
  [`OutboxRepository.cs:97`](../../../CoreBankDemo.PaymentsAPI/Outbox/OutboxRepository.cs#L97)
- Fourth subscription rule (both manifests) and its action
  [`subscription-transaction-events.yaml:16`](../../../dapr/components/subscription-transaction-events.yaml#L16)
  [`TransactionEventsController.cs:61`](../../../CoreBankDemo.PaymentsAPI/Controllers/TransactionEventsController.cs#L61)

**Operator console — confirmation, not contradiction**

- A `504` row is confirmed; a `202` row resolves to Cancelled; HTTP `Completed`/`Failed` is contradicted
  [`OperatorConsoleController.cs:1964`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L1964)
- Burst: tallied as `cancelled`, never `Rejected`; drains the proven leg
  [`OperatorConsoleController.cs:2051`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L2051)
- HTTP loop adds to the broadcast tally instead of overwriting it
  [`OperatorConsoleController.cs:946`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L946)
- `CancelledByBroadcast` kept apart so only accepted payments drain `Outstanding`
  [`OperatorModels.cs:370`](../../../CoreBankDemo.DemoRunner/Application/OperatorModels.cs#L370)
- Wire string copied, not referenced (ADR-015); parse arm
  [`IOutcomeFeed.cs:23`](../../../CoreBankDemo.DemoRunner/Application/Ports/IOutcomeFeed.cs#L23)
  [`DaprOutcomeFeed.cs:336`](../../../CoreBankDemo.DemoRunner/Infrastructure/DaprOutcomeFeed.cs#L336)

**Acceptance gate**

- Event stores hold `3 × completed + inbox.Cancelled`; an inbox cancel without its event fails
  [`LoadTestAssertionService.cs:527`](../../../CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs#L527)

**Peripherals — tests**

- Atomicity on real PostgreSQL with a ticking clock (tombstone, lost race, pending cancel, lost claim)
  [`TransactionCancellationHandlerTests.cs:55`](../../../tests/CoreBankDemo.Persistence.IntegrationTests/CoreBankApi/TransactionCancellationHandlerTests.cs#L55)
- Unit matrix: enqueue-before-save, detach on every non-applied outcome, replay publishes nothing
  [`TransactionCancellationHandlerTests.cs:427`](../../../tests/CoreBankDemo.CoreBankAPI.Tests/TransactionCancellationHandlerTests.cs#L427)
- Kernel keeps a pre-stamped `ProcessedAt`
  [`MarkAsCancelledAsyncTests.cs:46`](../../../tests/CoreBankDemo.Persistence.IntegrationTests/Messaging/MarkAsCancelledAsyncTests.cs#L46)
- Cancelled outcome recorded on a `Pending`/`Processing` row, never overwritten later
  [`OutboxRepositoryTests.cs:89`](../../../tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/OutboxRepositoryTests.cs#L89)
- Route table and both manifests carry the fourth rule
  [`TransactionEventIntakeWiringTests.cs:154`](../../../tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/TransactionEventIntakeWiringTests.cs#L154)
- Console: 504 confirmation, 202 resolution, contradiction, burst ordering (broadcast before/between submissions)
  [`OperatorConsoleControllerTests.cs:696`](../../../tests/CoreBankDemo.DemoRunner.Tests/Application/OperatorConsoleControllerTests.cs#L696)
  [`OperatorConsoleControllerTests.cs:803`](../../../tests/CoreBankDemo.DemoRunner.Tests/Application/OperatorConsoleControllerTests.cs#L803)
- ADR-015 drift guard pins all four copied wire strings to the manifests
  [`DaprComponentsProfileTests.cs:86`](../../../tests/CoreBankDemo.DemoRunner.Tests/Infrastructure/DaprComponentsProfileTests.cs#L86)
- Gate: cancel without event, event not yet processed, unchanged headline without cancellations
  [`LoadTestAssertionServiceTests.cs:465`](../../../tests/CoreBankDemo.LoadTestSupport.Tests/LoadTestAssertionServiceTests.cs#L465)
