# CoreBank is the only source of payment outcomes; infrastructure failures retry without limit

> **Status:** Implemented
> **Kind:** design spec
> **Original date:** 2026-09-18
> **Related:** [ADR-023](../../adr/ADR-023-corebank-sole-outcome-source.md); [ADR-020](../../adr/ADR-020-instant-rail-timeout-cancellation.md); [ADR-005](../../adr/ADR-005-resilience-testing-devproxy-k6.md); [story 5.4](2026-08-29-story-5-4-forwarding-processor-design.md); [constraints](../../constraints.md)

## Intent

**Problem:** An accepted payment can die without anybody being told. Observed live on 2026-09-17: a DemoRunner burst reported `still moving 4` for good. Those four payments were throttled (`429`) on PaymentsAPI's `POST /api/accounts/validate` call, used their five retries within about a second, and ended `Failed` in the Payments outbox. CoreBank never received them, so it published nothing; only CoreBank publishes outcome events, so the `202` caller and the console wait forever. 76 rows in the development database ended this way, every one of them on the validation call. This already contradicts `docs/constraints.md` invariants 2 ("every accepted payment reaches a terminal state") and 4 ("zero `Failed` after drain"); the load tests never trip it because they run without throttling.

Letting PaymentsAPI announce the failure itself was considered and rejected: `Failed` in the Payments outbox is already a claim about a transaction's outcome made without CoreBank, and publishing it would give transaction outcomes two sources. The same five-retry limit also guards the submission call, so a row could end `Failed` in PaymentsAPI while CoreBank had committed and published `Completed`.

**Approach:** One rule — *only CoreBank decides and announces how a payment ends.*

1. PaymentsAPI stops calling account validation before submitting. CoreBank's own check at execution is the only one.
2. Infrastructure failures are retried without limit, in all four message stores. The kernel never writes `Failed` again.
3. Ordering stays strict under faults: a batch stops at its first failure, so a failing row holds back everything behind it in its partition.
4. A `400` from CoreBank on submission is CoreBank's verdict: no retry. CoreBank records the rejection and publishes `transaction.failed` before it answers `400`.
5. CoreBank's two internal failures that answer `400` today answer `503`.

## Boundaries & Constraints

**Always:**
- Retry an infrastructure failure on the next poll tick; keep counting `RetryCount` for diagnostics. There is no backoff in the kernel: PaymentsAPI's HTTP calls already run through the standard resilience handler (`ServiceDefaults`, `AddStandardResilienceHandler`), which retries with exponential backoff and breaks the circuit.
- When a row fails, stop the batch and release the rest of the claimed rows to `Pending` with `RetryCount` unchanged.
- Commit a recorded rejection and its `transaction.failed` event in one save (transactional outbox, AD-5), exactly as the cancel tombstone does.
- Answer `400` from `POST /api/transactions/process` only after the rejection is durably stored. If the save fails, answer `503`.
- Treat `400` as a verdict for the submission call only. Every other non-2xx status, timeout and transport exception stays `Retry` — including `429`, `408`, `401`, `403`, `404`.
- Keep the client contract free of response bodies: PaymentsAPI learns only *that* the payment was rejected; the reason arrives with CoreBank's event.
- Coverage ≥90 % per project; TDD; PostgreSQL Testcontainer for persistence tests.

**Ask First:** Any new column on a message table; any new event type or topic; any change to the instant rail's budget, attempt or cancel settings; any new metric instrument.

**Never:**
- Publish a transaction outcome from PaymentsAPI.
- Write `Failed` as a kernel row status. (`MessageConstants.Status.Failed` stays: it is also the wire word for a business rejection inside a response payload.)
- Revive, migrate or delete rows that are already `Failed` in existing databases; `IsTerminal` keeps recognising them.
- Let a later row overtake a failed one in the same partition and priority.
- Overwrite an existing CoreBank inbox row with a rejection record; the existing row's outcome stands and the malformed request gets a plain `400`.
- Remove `POST /api/accounts/validate` from CoreBankAPI — it is part of the external contract (`docs/constraints.md` §2).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Infrastructure failure, standard rail | Submission answers `429`/`5xx`, times out, or throws | Row → `Pending`, `RetryCount + 1`; rest of the batch released to `Pending` untouched; retried next tick, without limit | Failure to record the retry: row stays `Processing`, reclaimed when stale (unchanged) |
| Throttle window (the observed bug) | `429` for 60 s on every call | Every row stays in flight; all deliver after the window resets; console's `still moving` drains to zero | N/A |
| Failure mid-batch | Rows 1–5 claimed, row 2 fails | Row 1 `Completed`; row 2 `Pending` (+1); rows 3–5 `Pending`, `RetryCount` unchanged; next tick claims row 2 first | Release conflicts with a concurrent claim → that row is left to its new owner, never forced; a row left `Processing` by a bookkeeping failure is only retried once its claim goes stale; released rows can pass it until then (ADR-023 Consequences) |
| Row that can never succeed | Handler throws every time | Partition blocked until fixed; a warning per attempt and a climbing `retry_scheduled` count make it visible | Accepted consequence — no automatic give-up |
| Unknown / inactive destination account | Valid request shape, account not in CoreBank | No pre-validation. CoreBank rejects at execution and publishes `transaction.failed`; Payments row `Completed` with a `Failed` payload; instant rail answers `200 Failed` | N/A |
| Request rejected at the door | `POST /process` fails model validation, `TransactionId` usable | CoreBank stores a terminal inbox row (`Completed`, `Failed` response, validation errors as reason) + `transaction.failed` outbox row in one save, then answers `400` | Save fails → `503`, nothing recorded, caller retries |
| Rejected at the door, id already known | A row for that `TransactionId` exists | Plain `400`; nothing is written; the existing row's own outcome stands (from PaymentsAPI a TransactionId's request never changes, so a known id that is rejected now *is* the recorded rejection) | N/A |
| Rejected at the door, unusable id | `TransactionId` missing or longer than 100 | Plain `400`, nothing recorded. Unreachable from PaymentsAPI (same id rules); only a hand-written request gets here | N/A |
| Rejected values do not fit the table | Account longer than 50, amount beyond `numeric(18,2)`, currency not 3 chars | Stored fields are truncated or zeroed; the reason carries the real validation errors | N/A |
| PaymentsAPI receives `400` on submission | Any rail | Client outcome `Rejected`; strategy returns a `Failed` submission without throwing; kernel marks the row `Completed`; no retry; instant rail answers `200 Failed` and records the `Rejected` metric outcome | N/A |
| Resend after a recorded rejection | Same `TransactionId` submitted again | CoreBank replays the stored `Failed` response with `200` | N/A |
| `400` on the cancel call | Instant rail's cancel | Unchanged: outcome unknown, claim released, residual `202`; the background submission then gets the verdict | N/A |
| CoreBank lost the store race and finds no row | Defensive path in `TransactionIntakeHandler` | `503` (was `400`) | Caller retries |
| Cancel cannot store its tombstone | `StoreFailed` | `503` (was `400`) | Caller treats it as unknown, as today |
| Instant payment behind a failing row | Earlier instant row in the partition keeps failing | Later instant rows wait within budget, then answer `504 Cancelled` locally (unchanged behaviour of `TryClaimByIdIfOldestAsync`) | N/A |
| Rows already `Failed` in an existing database | Pre-change data | Never claimed, never revived, still terminal | N/A |

## Code Map

- `CoreBankDemo.Messaging/MessageRepositoryBase.cs` -- `ApplyFailureTransition` always returns the row to `Pending`; new `ReleaseClaimsAsync` (`Processing` → `Pending`, `RetryCount` untouched, `Status` concurrency token, no-op on terminal rows, a conflicting row is skipped).
- `CoreBankDemo.Messaging/OutboxMessageRepositoryBase.cs`, `InboxMessageRepositoryBase.cs` -- drop the `RetryCount < MaxRetryCount` filter from `GetClaimableMessagesQuery`.
- `CoreBankDemo.Messaging/MessageConstants.cs` -- delete `Defaults.MaxRetryCount`.
- `CoreBankDemo.Messaging/IOutboxMessageStore.cs`, `IInboxMessageStore.cs` -- add `ReleaseClaimsAsync`.
- `CoreBankDemo.Messaging/OutboxProcessorBase.cs`, `InboxProcessorBase.cs` -- the batch loop stops at the first failed row and releases the remainder; the `TerminalFailed` outcome is no longer recorded.
- `CoreBankDemo.ServiceDefaults/BusinessMetrics.cs` -- remove `ItemOutcome.TerminalFailed`; remove any dashboard query that reads it (`observability/grafana/dashboards/`).
- `CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs` -- remove the validation call and its `IsValid=false` throw; map client outcome `Rejected` to a `Failed` `TransactionSubmission` stamped from `TimeProvider`.
- `CoreBankDemo.PaymentsAPI/Outbox/ICoreBankApiClient.cs`, `KiotaCoreBankApiClient.cs`, `CoreBankApiContracts.cs` -- remove `ValidateAccountAsync` and `AccountValidation`; add `CoreBankClientOutcome.Rejected`, classified for `ProcessTransactionAsync` only, the way the cancel's `409` is classified as `Conflict`.
- `CoreBankDemo.CoreBankAPI/Controllers/TransactionsController.cs` -- invalid model state on `process` goes through the new recording path; `TransportFailed` and the cancel's `StoreFailed` answer `503`.
- `CoreBankDemo.CoreBankAPI/Inbox/TransactionRejectionHandler.cs` (new, `ITransactionRejectionHandler`) -- modelled on `TransactionCancellationHandler`'s tombstone: store the rejection row and enqueue `transaction.failed` before one `StoreIfNewAsync`; detach the event row when the store is lost or throws. `TransactionIntakeHandler.cs` loses its now-dead "terminally failed during inline execution" branch.
- `CoreBankDemo.CoreBankAPI/OpenApi/corebank-api.json` -- document `503` on `process` and `cancel`.
- `CoreBankDemo.AppHost/devproxy/config/devproxy-errors.json` -- target `/api/transactions/process` instead of `/api/accounts/validate`.
- `CoreBankDemo.DemoRunner/Application/FaultLevels.cs` -- "Instant-rail jitter" preset becomes `5, 1200, 3000, 0`; comment explains the single proxied call per attempt. **Depends on PR #29 being merged.**
- Docs: ADR-023 (new); `docs/constraints.md` (§1 note on batch-stop, §2 `400`-after-record rule and `503`); `README.md` and `ARCHITECTURE.md` (single-step forward, status lists, retry description); `docs/backlog.md` (close the validation-rejection item at the instant-rail section); story 5.4 spec gets a "superseded in part by ADR-023" note.

## Acceptance

- Given CoreBank answers `429` to every call for 60 s, when a burst of 11 instant payments is sent, then no row ends `Failed`, every accepted payment is delivered after the window resets, and the console's `still moving` reaches zero.
- Given a batch of five claimed rows whose second row fails, when the tick ends, then rows three to five are `Pending` with their `RetryCount` unchanged and no later row completed before row two.
- Given a row has failed more than five times, when the next tick runs, then it is claimed and attempted again.
- Given a payment to an unknown destination account, when it is forwarded, then no validation call is made, CoreBank publishes `transaction.failed`, and the instant rail answers `200 Failed`.
- Given a `process` request that fails validation with a usable `TransactionId`, when CoreBank answers `400`, then an inbox row and a `transaction.failed` outbox row for that id were committed in the same save; and when that save fails, the answer is `503` and neither row exists.
- Given PaymentsAPI receives `400` on submission, then the row ends `Completed` with a `Failed` payload and is never attempted again; given `429`, `500` or a timeout, then it is retried.
- Given the "Instant-rail jitter" preset (1200–3000 ms) and a burst of 50 instant payments, then roughly two end cancelled (confirmed live; the figure is an estimate until then).
- The full load test passes every invariant unchanged.

## Design Notes

**Why no backoff in the kernel.** A stored backoff (a `HoldUntil` reuse or a `NextAttemptAt` column) was considered and dropped. With a batch that stops at its first failure, the failed row is first in line on the next tick, so the poll interval *is* a fixed retry delay and nothing needs storing. Hammering is already bounded one layer down: the resilience handler backs off and opens the circuit, and four partitions processing one row at a time means at most four delivery attempts in flight. The non-HTTP paths (CoreBank executing against its database, CoreBank publishing to Dapr) have no such handler and retry once per partition per tick — accepted for a demo. If it ever matters, an in-memory pause per partition in the processor fixes it without a column, because correctness does not depend on the pause.

**Why `401`/`403` get no special handling.** CoreBank has no authentication, so it cannot produce them; if one arrives it came from something in between and CoreBank never saw the request — so nothing could record or publish a rejection for it. They fall into "everything else is retried".

**Why `400` and not `200 Failed` for a door rejection.** A hand-written malformed request should still see `400`. PaymentsAPI handles both: `400` on first contact, and a replayed `200 Failed` on a resend, end in the same `Completed` row with a `Failed` payload.

**Load-test gate.** A recorded rejection looks like an execution-time rejection: a `Completed` inbox row with a `Failed` response and one event. The stage-cardinality formula (`3 × completed + cancelled`) already assumes a run without rejections; the k6 script sends none, so the gate is unchanged.

## Verification

**Commands:**
- `dotnet tool restore && dotnet test CoreBankDemo.UnitTests.slnf`
- `dotnet test CoreBankDemo.IntegrationTests.slnf`
- Full load test via the `load-test` skill.

**Manual:** the two live bursts in Acceptance (throttle at 10/60 s; jitter preset), run from the DemoRunner against the Regular AppHost.
