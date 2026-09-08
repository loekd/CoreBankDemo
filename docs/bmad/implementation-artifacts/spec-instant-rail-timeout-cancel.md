---
title: 'Instant rail: cancel on budget exhaustion and answer 504 instead of 202'
type: 'feature'
created: '2026-09-08'
status: 'done'
baseline_commit: '4054731928943c503086502e4cd450922ecafdf7'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/constraints.md'
  - '{project-root}/docs/adr/ADR-018-instant-payment-rail.md'
  - '{project-root}/.claude/skills/messaging-patterns/SKILL.md'
  - '{project-root}/.claude/skills/conventions/SKILL.md'
---

> Superseded in part by `spec-instant-rail-cancelled-event.md` (2026-09-08): a CoreBank-side cancellation now publishes a `transaction.cancelled` event, and the event-store gate is `3 × completed + inbox.Cancelled`.

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** When an instant payment's budget runs out, PaymentsAPI answers `202 Pending` and the background rail settles it later. A real SCT Inst rail gives a binary answer within its budget: settled or rejected, and "rejected" means nothing executed, so the payer may safely retry. Today the operator cannot demonstrate that — and an error cannot simply replace the `202`, because the queued row would still execute (a retry would then be a second payment).

**Approach:** Give the instant rail a cancellation path, then answer `504 Gateway Timeout` / `Status: Cancelled` only once the payment is provably dead. A new terminal kernel status `Cancelled` is added to both command stores. On budget exhaustion PaymentsAPI cancels: locally when the command never left PaymentsAPI; otherwise through a new CoreBankAPI `POST /api/transactions/cancel` that tombstones a not-yet-received command, cancels a still-`Pending` inbox row, or reports the committed outcome if CoreBank already executed it. Only when neither cancel nor a committed outcome can be established within budget does the honest `202 Pending` remain (the residual "investigation" case). The standard rail is untouched.

## Boundaries & Constraints

**Always:** Reserve the cancel allowance inside `BudgetMilliseconds` (forward phase = budget − `CancelTimeoutMilliseconds`), so a request thread is never held beyond the budget. Cancel a claimed outbox row while still holding the partition lock, so a later row in the same partition/priority cannot overtake it. Persist a `ProcessedAt` and a cached `Cancelled` `TransactionSubmission`/`TransactionResponse` payload on every cancelled row, so duplicate replay, `GET /api/transactions/{id}` and the ordering gate see a terminal row. Treat `Cancelled` as terminal everywhere terminality is decided (`IsTerminal`, `MarkAsFailedWithRetryAsync` guards, drain, summaries). Keep AD-5/AD-11: a cancel never touches the ledger and never publishes an event. Keep the checked-in OpenAPI document the owner of the new operation and let Kiota regenerate the client. Keep coverage ≥90 % per tier; TDD.

**Ask First:** Any change to the standard rail's responses; changing `PartitionCount`, lock lifetime or claim ordering; any new NuGet package; changing the k6 acceptance gate's invariants beyond making `Cancelled` a recognised terminal state.

**Never:** Answer `504` while the row could still be delivered. Cancel a row that is `Processing`, `Completed` or `Failed` (only `Pending` rows, and the row this request itself claimed, may be cancelled). Retry a business rejection. Map a residual unknown to anything but `202 Pending`. Add fields to the frozen `PaymentResponse`/`TransactionResponse` shapes — the outcome travels in `Status` and the HTTP code.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Settled in budget | `scheme=instant`, CoreBank healthy | `200` `Completed` (unchanged) | N/A |
| Business rejection | Insufficient funds | `200` `Failed` (unchanged) | N/A |
| Never reached CoreBank | Forward-phase budget spent waiting for the partition lock / turn, or lock backend failed | Claim row by id, `MarkAsCancelledAsync`, `504` `Cancelled` | Claim lost → row is in flight elsewhere → `202 Pending` |
| Attempts exhausted, CoreBank never stored it | Transport failure / attempt timeouts; `cancel` finds no inbox row | CoreBank stores a `Cancelled` tombstone (200 `Cancelled`); outbox row cancelled under the lock; `504` `Cancelled` | Late-arriving original `process` for that id replays the tombstone (200 `Cancelled`), never executes |
| Attempts exhausted, CoreBank row still `Pending` | CoreBank answered `202 Pending` or stored but did not execute | `cancel` claims and cancels the inbox row → `504` `Cancelled` | Claim lost in CoreBank → `409` with current status → PaymentsAPI `202 Pending` |
| Attempts exhausted, CoreBank already executed | Inbox row `Completed` (success or business rejection) | `cancel` answers `200` with the cached `TransactionResponse`; outbox row completed with that payload; `200` `Completed`/`Failed` | N/A |
| Cancel itself fails | `cancel` times out (`CancelTimeoutMilliseconds`), transport error, or `409` (`Processing`/`Failed`) | Claim released to `Pending` (unchanged path), `202 Pending`, metric `deferred` | Residual honest unknown; documented |
| Duplicate key of a cancelled instant row | Same `Idempotency-Key` resent | Replay `504` `Cancelled` with the persisted `ProcessedAt`; no new row, no delivery | N/A |
| Rail disabled | `Enabled=false` | `202 Pending` as standard (unchanged) | N/A |
| Standard rail | `scheme` absent/`standard` | Byte-identical to today; a standard row is never cancelled | N/A |
| Load-test drain/assert | Run with cancelled instant rows | Drain completes; `Cancelled` counted terminal; `outbox.Completed == inbox.Completed == msgOutbox/3 == paymentsInbox/3`; `outbox.Total == Completed + Cancelled == k6 unique submitted`; balances conserved; ordering gate green | `Failed`/`Pending`/`Processing` still fail the gate |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.Messaging/MessageConstants.cs:14-22` -- add `Status.Cancelled`; tests enumerate statuses at `tests/CoreBankDemo.Messaging.Tests/MessageConstantsTests.cs:33-39`.
- `CoreBankDemo.Messaging/MessageRepositoryBase.cs` -- `IsTerminal` :470 (add Cancelled); `MarkAsFailedWithRetryAsync` guards :341/:372 use `== Failed` → must use `IsTerminal` or a Cancelled row is revived to Pending; model `MarkAsCancelledAsync` on `MarkAsCompletedAsync` :426-468 (concurrency-token save, `AlreadyTerminal` no-op, sets `ProcessedAt`, `LastError=reason`). Claim queries (`OutboxMessageRepositoryBase.cs:37-47`, `InboxMessageRepositoryBase.cs:89-99`) allow-list Pending/stale Processing — no change.
- `CoreBankDemo.Messaging/IOutboxMessageStore.cs`, `IInboxMessageStore.cs` -- add `MarkAsCancelledAsync(TMessage, string reason, ct) → Task<MessageTransitionOutcome>` (mirror members). Persistence tests: `tests/CoreBankDemo.Persistence.IntegrationTests/Messaging/MarkAsCompletedAsyncTests.cs:99-128` and `MarkAsFailedWithRetryAsyncTests.cs` are the templates.
- `CoreBankDemo.PaymentsAPI/Models/InstantRailOptions.cs` + `InstantPaymentRailServiceCollectionExtensions.cs` -- add `CancelTimeoutMilliseconds` (default 1500); validation `Attempt×MaxAttempts + Cancel ≤ Budget`.
- `CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs` -- forward deadline = start + Budget − Cancel (both the wait loop :91 and attempts :200); new `InstantDeliveryOutcome.Cancelled`; local cancel via `TryClaimByIdAsync` + `MarkAsCancelledAsync` for the never-forwarded paths (:99, :133, :162); after :318 (attempts exhausted, still under the lock) call `forwarder.CancelAsync` with the cancel allowance and map per matrix; `Deferred` only on the residual. Tests: `tests/CoreBankDemo.PaymentsAPI.Tests/InstantPaymentForwardingHandlerTests.cs` (`Deferred` assertions at :82,:94,:113,:154,:234,:356,:447,:488 become Cancelled/Completed per matrix).
- `CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs:37` `ICoreBankTransactionForwarder` -- add `CancelAsync(OutboxMessage, ct) → Task<TransactionSubmission?>` (null = unknown); persists `ResponsePayload` like `ForwardAsync` :148.
- `CoreBankDemo.PaymentsAPI/Outbox/ICoreBankApiClient.cs:13`, `KiotaCoreBankApiClient.cs:41` -- add `CancelTransactionAsync(TransactionSubmissionRequest, ct) → CoreBankResult<TransactionSubmission>`; 409 → a distinct non-retry outcome carrying the status; header handling as :136-148. Tests: `CoreBankApiClientTests.cs` (`FakeHttpMessageHandler` :778).
- `CoreBankDemo.CoreBankAPI/OpenApi/corebank-api.json` -- add `POST /api/transactions/cancel` (`CancelTransaction`), body `TransactionRequest`, `200` `TransactionResponse`, `409` `TransactionResponse`, `400` `ErrorResponse`. Kiota regenerates via `CoreBankDemo.PaymentsAPI.csproj:64-85`.
- `CoreBankDemo.CoreBankAPI/Inbox/TransactionIntakeHandler.cs` -- new `TransactionCancellationHandler` (own file, `ITransactionCancellationHandler`) reusing `IInboxMessageRepository.FindByIdempotencyKeyAsync` :108, `StoreIfNewAsync` (tombstone: same columns as :118-133, `Status=Cancelled`, `ProcessedAt`, `ResponsePayload`), `IInboxMessageStore.TryClaimByIdAsync` + `MarkAsCancelledAsync`. Also `BuildIntakeResultForExisting` :223-250: a `Cancelled` row replays like `Completed` (200 cached payload). `TransactionsController.cs` -- new thin action; `GET` :114 already serves cached payload.
- `CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs` -- `ToStoredResultAsync` :79 maps `Cancelled` → `StatusCode(504, PaymentResponse{Status=Cancelled})`; `ToDuplicateResult` :118 replays `504 Cancelled` for a `Cancelled` row via `ResolveDeliveredResponse`. Tests `PaymentsControllerTests.cs:322,:427,:527,:550`.
- `CoreBankDemo.ServiceDefaults/BusinessMetrics.cs:71-76,:325-331` -- add `InstantPaymentOutcome.Cancelled`; `tests/.../BusinessMetricsTests.cs:106-111` add row.
- `CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs` -- `Summarize` :334-338 (Cancelled terminal, add `Cancelled` count to `MessageStoreSummary`), `allSubmittedProcessed` :439, `stageCardinality` :465-482, `expectedUniqueProcessed` :434 per matrix; `McpTools/LoadTestTools.cs:88`. Tests: `tests/CoreBankDemo.LoadTestSupport.Tests/LoadTestAssertionServiceTests.cs`, `tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/*`.
- `k6/script.js` -- :220-226, :244-250 admit `504`+`Cancelled`; per-request `responseCallback: http.expectedStatuses(200, 202, 504)` on instant calls so `http_req_failed` stays honest; new `instant_cancelled` counter. `tests/CoreBankDemo.LoadTestSupport.Tests/K6ScriptContractTests.cs` guards the script.
- `CoreBankDemo.DemoRunner` -- `Application/OperatorModels.cs:77-97` (`PaymentOutcome.Cancelled`, `PaymentTrackingState.Cancelled`); `Infrastructure/HttpPaymentGateway.cs:154-212` (admit 504 body, map `cancelled`); `Application/OperatorConsoleController.cs:2366` (admit 504), :1529 status text, :1545 evidence, :2039 tracking (proven, not `Awaiting`; a later event → `Contradiction`), :883-943 burst counting (`cancelled` tally, not a transport failure); `Terminal/MainWindow.cs:1583` surface it. Tests: `HttpPaymentGatewayTests.cs`, `OperatorConsoleControllerTests.cs:548`.
- Docs: new `docs/adr/ADR-020-instant-rail-timeout-cancellation.md` (019 was already taken); `README.md:19,:139-143,:254,:334-338`; `CoreBankDemo.LoadTests/AppHost.cs:66-72` comment; `demo-requests.http:25-40` add a cancelled case; `ARCHITECTURE.md` matrix.

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.Messaging/*` -- `Status.Cancelled`, `MarkAsCancelledAsync` on both ports and the base, `IsTerminal`/failure-guard fixes; unit + persistence tests (no-op on terminal, never claimed/reclaimed, never revived) -- kernel first, everything else depends on it.
- [x] `CoreBankDemo.CoreBankAPI/*` -- cancel handler, controller action, intake replay of `Cancelled`, OpenAPI operation; tests for tombstone, pending-cancel, already-executed, in-flight 409, store race.
- [x] `CoreBankDemo.PaymentsAPI/*` -- options, client + forwarder `CancelAsync`, handler cancel paths, controller `504` mapping + duplicate replay, metric; tests for every matrix row.
- [x] `CoreBankDemo.LoadTestSupport/*`, `k6/script.js` -- terminal `Cancelled`, adjusted cardinality/processed assertions, k6 checks and expected statuses.
- [x] `CoreBankDemo.DemoRunner/*` -- outcome, gateway, rail semantics, tracking/burst/status text; tests.
- [x] Docs -- ADR-020 (the ADR-019 number was already taken), README, AppHost comment, `demo-requests.http`, ARCHITECTURE.md.

**Acceptance Criteria:**
- Given a fresh instant payment whose forward phase never reaches CoreBank, when the budget expires, then the caller gets `504`/`Cancelled` within `BudgetMilliseconds`, the outbox row is `Cancelled` with `ProcessedAt`, and the background processor never delivers it.
- Given CoreBank stored the command but did not execute it, when PaymentsAPI cancels, then the inbox row becomes `Cancelled`, no ledger movement or event occurs, and a duplicate `process` for that id replays `200`/`Cancelled`.
- Given CoreBank executed the command before the cancel arrived, when PaymentsAPI cancels, then the caller receives the committed `200` outcome, not `504`.
- Given the cancel call itself fails or finds the row `Processing`, when the budget expires, then the caller receives `202 Pending` and the row is later settled by the background rail exactly once.
- Given a mixed-rail load run under the latency preset, when drained, then `get_assertion_results` passes with cancelled rows present.

## Design Notes

**Why `504`.** After cancellation the timeout is no longer ambiguous: nothing executed, and the same idempotency key replays the same answer, so a client retry with a new key is safe — exactly the SCT Inst time-out rejection. `504` says "the settlement did not answer in time"; the body's `Status: Cancelled` carries the business meaning in the frozen shape.

**Two-phase cancel, in this order** (under the partition lock the request already holds):
```
attempts exhausted
  → CancelAsync(message) within CancelTimeout
      200 Cancelled            → MarkAsCancelledAsync(outbox) → 504
      200 Completed|Failed     → payload + MarkAsCompletedAsync → 200
      409 / timeout / error    → MarkAsFailedWithRetryAsync (release) → 202
```
The never-forwarded paths cancel locally without calling CoreBank: the command cannot exist there.

**Tombstone.** The cancel body is the original `TransactionRequest`, so the tombstone row has the same columns as a real command; `StoreIfNewAsync` makes "cancel arrives before the original" and "original arrives before the cancel" symmetric under the unique key (AD-4).

**Dev Proxy is not bypassed.** The cancel call travels through the same faulted hop as the payment leg (existing `urlsToWatch` patterns already match `/api/transactions/cancel`; no config change). Under the "Instant-rail overrun" latency preset the cancel is therefore delayed too and typically ends as the residual `202` — the demo shows honestly that a timeout on the cancellation channel is still an unknown. The `504` is demonstrated when CoreBank never received or executed the command (transport rejection, validate failure, partition-lock starvation).

## Verification

**Commands:**
- `dotnet tool restore && dotnet build CoreBankDemo.sln` -- expected: clean build, Kiota regenerated with `CancelTransaction`.
- `dotnet test CoreBankDemo.UnitTests.slnf` -- expected: green, ≥90 % line per project.
- `dotnet test CoreBankDemo.IntegrationTests.slnf` -- expected: green (Docker required).
- `load-test` skill run (default profile, then the latency preset) -- expected: `get_assertion_results` passes in both; `inlineInstantSettlement` observed; any `Cancelled` rows counted terminal.

## Suggested Review Order

**Entry point — the two-phase cancel decision**

- Attempts exhausted: cancel through CoreBank, map Cancelled/committed/unknown under the partition lock
  [`InstantPaymentForwardingHandler.cs:305`](../../../CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs#L305)
- Forward phase ends a cancel allowance before the budget, so the thread never overruns it
  [`InstantPaymentForwardingHandler.cs:99`](../../../CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs#L99)
- Never-forwarded paths cancel locally: the command cannot exist at CoreBank
  [`InstantPaymentForwardingHandler.cs:122`](../../../CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs#L122)
- Review fix: a forwarded result survives a lock backend that throws afterwards
  [`InstantPaymentForwardingHandler.cs:155`](../../../CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs#L155)
- Decision record: why 504 is only safe after a proven cancel; residual 202 documented
  [`ADR-020:23`](../../adr/ADR-020-instant-rail-timeout-cancellation.md#L23)

**CoreBank side — tombstone, pending-cancel, committed replay**

- Tombstone via `StoreIfNewAsync`: original-before-cancel and cancel-before-original are symmetric
  [`TransactionCancellationHandler.cs:67`](../../../CoreBankDemo.CoreBankAPI/Inbox/TransactionCancellationHandler.cs#L67)
- Pending row: claim by id, then cancel; Processing/Completed/Failed are never touched
  [`TransactionCancellationHandler.cs:164`](../../../CoreBankDemo.CoreBankAPI/Inbox/TransactionCancellationHandler.cs#L164)
- A late original for a cancelled id replays the tombstone and never executes, even inline
  [`TransactionIntakeHandler.cs:199`](../../../CoreBankDemo.CoreBankAPI/Inbox/TransactionIntakeHandler.cs#L199)
- Thin action: 200 cancelled/committed, 409 in flight, 400 store race
  [`TransactionsController.cs:120`](../../../CoreBankDemo.CoreBankAPI/Controllers/TransactionsController.cs#L120)
- Contract owner for the new operation; 409 body is a separate schema so Kiota yields a catchable error
  [`corebank-api.json:141`](../../../CoreBankDemo.CoreBankAPI/OpenApi/corebank-api.json#L141)

**Kernel — `Cancelled` as a terminal status**

- Fifth status; claim queries already allow-list Pending/stale-Processing so it is never claimed
  [`MessageConstants.cs:26`](../../../CoreBankDemo.Messaging/MessageConstants.cs#L26)
- `MarkAsCancelledAsync`: concurrency-token save; conflict is withheld, never re-applied
  [`MessageRepositoryBase.cs:524`](../../../CoreBankDemo.Messaging/MessageRepositoryBase.cs#L524)
- `IsTerminal` now guards the failure transition too — a cancelled row can never be revived
  [`MessageRepositoryBase.cs:341`](../../../CoreBankDemo.Messaging/MessageRepositoryBase.cs#L341)
- New transition outcome for "withheld because another claim owns the row"
  [`MessageTransitionOutcome.cs:17`](../../../CoreBankDemo.Messaging/MessageTransitionOutcome.cs#L17)

**PaymentsAPI wire and client**

- Fresh Cancelled → 504 with the frozen `PaymentResponse` shape; duplicate replays the same 504
  [`PaymentsController.cs:97`](../../../CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs#L97)
- Forwarder `CancelAsync`: null means unknown; conflict status is logged before collapsing
  [`HttpForwardOutboxDeliveryStrategy.cs:52`](../../../CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs#L52)
- Background rail marks the outbox row Cancelled when CoreBank replays a cancellation (own try/catch)
  [`HttpForwardOutboxDeliveryStrategy.cs:130`](../../../CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs#L130)
- Kiota adapter maps the generated 409 error to `Conflict`, never a retry
  [`KiotaCoreBankApiClient.cs:190`](../../../CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs#L190)
- A cached cancellation is a committed outcome; later events never overwrite it
  [`OutboxRepository.cs:94`](../../../CoreBankDemo.PaymentsAPI/Outbox/OutboxRepository.cs#L94)
- `CancelTimeoutMilliseconds` reserved inside the budget; validated at startup
  [`InstantRailOptions.cs:54`](../../../CoreBankDemo.PaymentsAPI/Models/InstantRailOptions.cs#L54)

**Acceptance gate**

- Cancelled counted terminal from the payments outbox (covers local cancels CoreBank never saw)
  [`LoadTestAssertionService.cs:238`](../../../CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs#L238)
- Cancelled rows excluded from FIFO comparison: they never executed, so have no execution order
  [`LoadTestAssertionService.cs:497`](../../../CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs#L497)
- `poll_until_drained` counts cancelled as processed
  [`LoadTestTools.cs:68`](../../../CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs#L68)
- k6 admits 504 only with a `Cancelled` body; instant calls only
  [`script.js:79`](../../../k6/script.js#L79)

**Operator console**

- 504 admitted only with a Cancelled body; standard rail still 202-only
  [`HttpPaymentGateway.cs:160`](../../../CoreBankDemo.DemoRunner/Infrastructure/HttpPaymentGateway.cs#L160)
- Cancelled row is proven by HTTP alone, never awaits; a later broadcast is a contradiction
  [`OperatorConsoleController.cs:2056`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L2056)
- New outcome and tracking state
  [`OperatorModels.cs:88`](../../../CoreBankDemo.DemoRunner/Application/OperatorModels.cs#L88)

**Peripherals**

- Metric attribute `outcome=cancelled`
  [`BusinessMetrics.cs:66`](../../../CoreBankDemo.ServiceDefaults/BusinessMetrics.cs#L66)
- Handler matrix tests (tombstone, committed-before-cancel, cancel timeout, lock-throw-after-result)
  [`InstantPaymentForwardingHandlerTests.cs:510`](../../../tests/CoreBankDemo.PaymentsAPI.Tests/InstantPaymentForwardingHandlerTests.cs#L510)
- CoreBank cancel handler tests
  [`TransactionCancellationHandlerTests.cs:21`](../../../tests/CoreBankDemo.CoreBankAPI.Tests/TransactionCancellationHandlerTests.cs#L21)
- Kernel transition on real PostgreSQL, including the withheld-conflict case
  [`MarkAsCancelledAsyncTests.cs:17`](../../../tests/CoreBankDemo.Persistence.IntegrationTests/Messaging/MarkAsCancelledAsyncTests.cs#L17)
- Gate tests reproducing the A/B ordering inversion a cancelled row would otherwise cause
  [`LoadTestAssertionServiceTests.cs:371`](../../../tests/CoreBankDemo.LoadTestSupport.Tests/LoadTestAssertionServiceTests.cs#L371)
