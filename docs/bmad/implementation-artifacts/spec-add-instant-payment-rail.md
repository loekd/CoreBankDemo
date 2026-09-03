---
title: 'Add instant-payment rail (SCT Inst) alongside store-and-forward'
type: 'feature'
created: '2026-09-02'
status: 'done'
baseline_commit: '73de76296d8566f10a1452cf50195f0ef1d11424'
review_loop_iteration: 1
context:
  - '{project-root}/docs/bmad/constraints.md'
  - '{project-root}/docs/bmad/planning-artifacts/architecture/architecture-CoreBankDemo-2026-08-21/ARCHITECTURE-SPINE.md'
  - '{project-root}/.claude/skills/messaging-patterns/SKILL.md'
  - '{project-root}/.claude/skills/conventions/SKILL.md'
  - '{project-root}/.claude/skills/observability/SKILL.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** CoreBankDemo models exactly one payment rail. Every payment is accepted with `202`, buffered in the payments outbox, and settled by a background processor; CoreBankAPI likewise stores every command in its inbox and executes it on a processor loop. SEPA has two rails with materially different engineering constraints: an SCT Inst payment gives the payer's PSP, the payee's PSP and the clearing mechanism **nine seconds in total**, runs 24/7/365 and has no scheme-level amount limit, while a standard SCT settles in one to two business days in batches. Because the demo only has the batch shape, it cannot show a bounded end-to-end budget, an ambiguous timeout on a synchronous hop, or why retry, circuit-breaker and idempotency choices differ between the two rails — the properties that motivate most of the architecture.

**Approach:** Add an opt-in instant rail **alongside** the existing one and remove nothing. A payment may declare `scheme: instant`; PaymentsAPI then makes one budgeted, inline attempt to forward the command before answering the caller, and CoreBankAPI may execute a command inline in the request thread instead of deferring it to its inbox processor. Persistence, dedupe, partitioning, atomic commit, event publication and drain semantics are unchanged. When the budget is exhausted, the row is already claimed, or the inline attempt fails, both services fall back to exactly the machinery that runs today, so no-loss and exactly-once continue to hold and the existing acceptance gate keeps passing.

## Boundaries & Constraints

**Always:** Keep the standard rail byte-identical when `scheme` is absent or `standard`. Persist and dedupe **before** any inline attempt, so no request can bypass the payments outbox row or the CoreBank inbox row. Claim the outbox row through the existing kernel claim path before an inline delivery, so an inline attempt can never race the background processor into a double delivery. Preserve AD-5: inline CoreBank execution commits the ledger mutation, the inbox completion with cached response, and the event enqueue in one database transaction. Preserve AD-11: a business rejection is a completed message with a cached failure payload, never a retry and never a transport failure. Propagate `traceparent`/`tracestate` on the inline hop exactly as on the background hop (AD-8). Validate the new options at startup with the Story 3.1 pattern. Keep the four-store drain semantics and the k6 acceptance gate green without editing either.

**Ask First:** Any change to status codes, payloads or headers on the standard rail; removing, bypassing or short-circuiting the payments outbox or the CoreBank inbox; changing drain or assertion semantics, `LoadTestSupport`, or `k6/script.js`; any change to the checked-in CoreBank OpenAPI document beyond additive optional fields; a new package, container or external dependency; changing `PartitionCount`, lock lifetime or claim semantics; adding a channel or front-end (explicitly out of scope here).

**Never:** Execute a transfer without a persisted, deduped row. Return `200 OK` unless CoreBankAPI has confirmed a committed outcome. Treat a budget timeout as either success or failure of the payment — it is an unknown, and the row stays Pending. Retry a business rejection. Deliver inline while another replica holds the partition lock. Hold a request thread beyond the configured budget. Put the scheme, ids, account numbers or any unbounded value into metric attributes beyond the closed set defined below. Introduce a second code path for ledger execution — inline execution reuses the existing handler.

## Wire contract additions (additive only)

**PaymentsAPI — `POST /api/payments`**

Optional body field `scheme`, a closed set of `standard` (default) and `instant`. Absent or `standard` reproduces today's behaviour exactly.

| Instant-rail condition | Response |
|---|---|
| CoreBank confirms a committed success within budget | `200 OK`, status `Completed` |
| CoreBank confirms a business rejection within budget | `200 OK`, status `Failed`, with the rejection reason |
| Budget exhausted, transport failure, or the row is already claimed | `202 Accepted`, status `Pending` — background delivery continues unchanged |
| Duplicate idempotency key | Replay the stored snapshot: `200 OK` if the row is `Completed`, otherwise `202 Accepted` |

**CoreBankAPI — `POST /api/transactions/process`**

Optional request header `X-Execute-Mode: inline`. Absent reproduces today's deferred behaviour exactly.

| Inline condition | Response |
|---|---|
| New command, inline execution commits | `200 OK` with the final `TransactionResponse` |
| New command, inline execution throws | `202 Accepted`; the row stays `Pending` and the inbox processor drains it |
| Duplicate, already `Completed` | `200 OK` with the cached response (unchanged Replayed path) |
| Duplicate, still in flight | `202 Accepted` with current status (unchanged AD-11) |

**New options — `Payments:InstantRail`**

`Enabled` (default `true`), `BudgetMilliseconds` (default `9000`), `AttemptTimeoutMilliseconds` (default `2500`), `MaxAttempts` (default `2`). Validated at startup: every value positive, and `AttemptTimeoutMilliseconds` × `MaxAttempts` must not exceed `BudgetMilliseconds`.

## Metric contract additions

| Instrument | Type / Unit | Required attributes | Recorded at |
|---|---|---|---|
| `corebankdemo.payment.intake` | Counter / `{payment}` | existing `outcome`, plus `payment.scheme=standard\|instant` | unchanged point; the attribute is added |
| `corebankdemo.payment.instant.duration` | Histogram / `ms` | `outcome=settled\|rejected\|deferred` | once per instant-rail request, when the inline attempt concludes or the budget expires |

No other instrument changes. `payment.scheme` is a closed two-value set and is never copied from request data verbatim.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected behaviour | Error handling |
|---|---|---|---|
| Standard payment | `scheme` absent | Identical to today: stored, `202`, background delivery | Unchanged |
| Instant, happy path | `scheme=instant`, CoreBank healthy | Row stored and claimed, inline forward with `X-Execute-Mode: inline`, CoreBank commits, row completed, `200 OK` `Completed` | N/A |
| Instant, business rejection | Insufficient funds | CoreBank commits a rejection and its failure event; row completed with cached failure payload; `200 OK` `Failed` | Never retried, never counted as transport failure |
| Instant, CoreBank slow | Attempt exceeds `AttemptTimeoutMilliseconds` | Retry while budget remains; on exhaustion release the claim, `202` `Pending` | Row remains Pending for the processor |
| Instant, budget exhausted mid-retry | Elapsed ≥ `BudgetMilliseconds` | Stop immediately, release the claim, `202` `Pending` | No further attempt is started |
| Instant, CoreBank returns 5xx | Transport failure | Counts toward retry, not toward business outcome | Existing retry/circuit-breaker policy applies |
| Instant, reply lost after commit | CoreBank committed, response never arrived | Row stays Pending; background delivery replays the same idempotency key; CoreBank returns the cached response | Exactly-once preserved by CoreBank dedupe |
| Instant, row already claimed | Processor holds the partition | Skip the inline attempt entirely, `202` `Pending` | No double delivery |
| Instant, duplicate key while pending | Same key resubmitted | Replay snapshot, `202` `Pending` | No second row, no second attempt |
| Instant, duplicate key after completion | Same key resubmitted | Replay snapshot, `200 OK` | No re-execution |
| Instant rail disabled | `Enabled=false` | `scheme=instant` behaves as `standard`, `202` | Documented, not an error |
| Inline execution throws in CoreBank | Ledger transaction rolls back | Row remains `Pending`, `202`; inbox processor executes it later | No partial commit, no event published |
| Cancellation | Client disconnects mid-attempt | Existing cancellation propagation; row left Pending | No measurement recorded solely because of cancellation |
| Invalid `scheme` value | e.g. `scheme=express` | `400 Bad Request` with a validation error | Closed set enforced by model validation |

</frozen-after-approval>

## Code Map

- `docs/adr/ADR-018-instant-payment-rail.md` (new) — records the additive contract change required by AD-1 and the response semantics extension to AD-11. Write this first; the story is a behaviour change and the architecture spine forbids silent divergence.
- `CoreBankDemo.PaymentsAPI/Models/PaymentRequest.cs` — add the optional `Scheme` property with closed-set validation and a default of `standard`; keep every existing attribute untouched.
- `CoreBankDemo.PaymentsAPI/Models/` — options record for `Payments:InstantRail` with startup validation, following `spec-3-1-validated-processing-options`.
- `CoreBankDemo.PaymentsAPI/Handlers/PaymentStorageHandler.cs` — unchanged for the standard rail. The instant rail is a **new** handler/decorator that runs after storage; do not fold the budget loop into the storage handler (AD-2: the storage handler stays pure).
- `CoreBankDemo.PaymentsAPI/Outbox/OutboxRepository.cs`, `PaymentsOutboxProcessor.cs`, `HttpForwardOutboxDeliveryStrategy.cs` — reuse the existing claim, deliver and complete paths for the inline attempt. Extract whatever the processor already does per item so both callers share it; do not copy it.
- `CoreBankDemo.PaymentsAPI/Outbox/ICoreBankApiClient.cs`, `KiotaCoreBankApiClient.cs` — carry the `X-Execute-Mode` header on the inline call only.
- `CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs` — map the new outcomes to `200`/`202`; stays thin.
- `CoreBankDemo.CoreBankAPI/Controllers/TransactionsController.cs` — read the optional header, pass an execution mode to the handler, map an inline committed result to `200 OK`.
- `CoreBankDemo.CoreBankAPI/Inbox/TransactionIntakeHandler.cs` — after `StoreIfNewAsync` reports a new row, optionally invoke the existing execution path inline.
- `CoreBankDemo.CoreBankAPI/Inbox/TransactionExecutionHandler.cs` — the single ledger-execution path, called by both the inbox processor and the inline route. No behavioural change; extract a callable seam if one does not already exist.
- `CoreBankDemo.ServiceDefaults/BusinessMetrics.cs` — add the `payment.scheme` attribute and the instant-duration histogram to the closed vocabulary.
- `tests/CoreBankDemo.PaymentsAPI.Tests`, `tests/CoreBankDemo.CoreBankAPI.Tests`, `tests/CoreBankDemo.ServiceDefaults.Tests` — unit coverage for every matrix row reachable with mocked ports.
- `tests/CoreBankDemo.Persistence.IntegrationTests` — Postgres coverage for inline commit, inline rollback leaving the row Pending, claim contention, and duplicate replay.
- `demo-requests.http` — add an instant-rail example next to the existing one, using seeded demo accounts and a fixed idempotency key.

### Code Map additions (review loop 1 — see Spec Change Log)

- `CoreBankDemo.PaymentsAPI/Outbox/OutboxMessage.cs` (or wherever the entity is declared) — add a nullable `ResponsePayload` column, mirroring CoreBank's own `InboxMessage.ResponsePayload`. Populate it with the serialized `TransactionSubmission`/outcome on **every** completed delivery — both `InstantPaymentForwardingHandler`'s inline path and `HttpForwardOutboxDeliveryStrategy`'s background path — not only the instant rail, so the column is never an instant-only special case. This is a schema change; no EF migration is involved (this repo provisions schema via `EnsureCreatedAsync()` everywhere — never migrations), so the new column takes effect the next time the database is recreated.
- `PaymentSnapshot` and `PaymentsController.ToDuplicateResult`/`ToResponse` — when replaying a duplicate whose row is `Completed`, derive the wire `Status` (`Completed` vs `Failed`, with the rejection reason if present) from the persisted `ResponsePayload`, not from the row's raw kernel `Status` column (which never distinguishes settled from rejected — AD-11). This is what makes "replay the stored snapshot" in the I/O matrix actually correct for a rejected instant payment. While in this area, also fix the related gap where a duplicate hitting a row still claimed (`Status = Processing`) surfaces that internal value verbatim instead of the matrix's documented `Pending` wire word.
- `CoreBankDemo.CoreBankAPI/CoreBankDbContext.cs` — call `MessageRepositoryBase<InboxMessage, CoreBankDbContext>.ConfigureConcurrencyToken(entity)` for the transaction-command `InboxMessage` entity (it is currently configured for `PaymentsDbContext`'s entities only — never for CoreBankAPI's). Run the full existing CoreBankAPI + Persistence.IntegrationTests suites afterward to confirm this foundational change doesn't alter any pre-existing background-processor claim behavior.
- `CoreBankDemo.CoreBankAPI/Inbox/TransactionIntakeHandler.cs` — before `TryExecuteInlineAsync` invokes `executionHandler.HandleAsync`, claim the row via the id-based claim inherited from `MessageRepositoryBase` (mirroring `IOutboxMessageStore<OutboxMessage>.TryClaimByIdAsync` added for PaymentsAPI) so a concurrent background-processor batch claim on the same row can never also win it — this closes a real double-ledger-execution race: the background `InboxProcessorBase` protects itself with a partition lock before claiming, but the inline path currently calls the shared execution handler with no lock or claim at all, and `StoreIfNewAsync` commits (making the row externally visible) before the inline call's own transaction opens. If the claim fails, fall back to `Accepted`/`Pending` exactly like an already-claimed row on the Payments side.

## Tasks & Acceptance

**Execution:**
- [x] Write `ADR-018` recording the additive contract change and its rationale before touching code.
- [x] Add validated `Payments:InstantRail` options; prove startup failure on an over-budget attempt configuration.
- [x] Add the optional `Scheme` field with closed-set validation; prove an unknown value is a `400` and an absent value is `standard`.
- [x] Extract the per-item claim/deliver/complete sequence the outbox processor already performs so the inline path calls the same code.
- [x] Implement the budgeted inline forward: claim, attempt with per-attempt timeout, stop on budget exhaustion, always release the claim, never exceed the budget.
- [x] Add `X-Execute-Mode: inline` to the CoreBank client for inline calls only, with trace context propagated as on the background hop.
- [x] Implement CoreBank inline execution reusing the existing execution handler and its atomic commit; leave the row Pending if it throws.
- [x] Map both controllers' new outcomes to `200`/`202` without changing standard-rail responses.
- [x] Add the two metric contract additions and assert them with `MeterListener`.
- [x] Add the instant example to `demo-requests.http`.
- [x] (review loop 1) Add a `ResponsePayload` column to Payments' `OutboxMessage`, populated on every completed delivery (inline and background); derive duplicate-replay's wire status from it, not from the raw kernel `Status`; prove a resubmitted business rejection replays as `Failed`, not `Completed`.
- [x] (review loop 1) Add a row-level claim for CoreBank's `InboxMessage` (mirroring `TryClaimByIdAsync`) before inline execution, and configure `InboxMessage.Status` as an EF concurrency token in `CoreBankDbContext`; prove a concurrent background batch-claim and the inline claim can never both win the same row.
- [x] Run the focused test projects, the full rebuild solution gate, and the existing k6 acceptance gate unchanged.

**Acceptance Criteria:**
- Given a request without `scheme`, when it is processed, then the response, persistence, events and metrics are byte-identical to the baseline commit.
- Given `scheme=instant` and a healthy CoreBank, when the payment is submitted, then the caller receives `200 OK` with a committed outcome, the outbox row is `Completed`, and exactly one ledger execution occurred.
- Given `scheme=instant` and a CoreBank that exceeds the budget, when the payment is submitted, then the caller receives `202 Accepted` with status `Pending`, the claim is released, and the background processor later completes the row without a second ledger execution.
- Given `scheme=instant` and a CoreBank that commits but whose reply is lost, when the background processor replays the command, then CoreBank returns the cached response and the ledger is mutated exactly once.
- Given `scheme=instant` and a row already claimed by a processor, when the inline path runs, then it makes no delivery attempt and answers `202`.
- Given an inline CoreBank execution that throws, when the transaction rolls back, then no event is published, the inbox row remains `Pending`, and the inbox processor completes it on its next pass.
- Given a business rejection on the instant rail, when it commits, then the caller receives `200 OK` with a failure status, the row is `Completed`, and no retry or transport-failure metric is recorded.
- Given the full rebuild solution, when tested, then all tests pass and every logic project retains at least 90% line coverage.
- Given the existing k6 acceptance gate, when it is run unchanged, then exactly-once, no-loss, balance-conservation, per-key ordering and four-store drain all still pass.

## Design Notes

**Why the instant path still writes to the outbox.** The inline attempt is an optimisation in front of the existing pipeline, not a replacement for it. Persisting first is what keeps the no-loss invariant true when the process dies mid-attempt, and it is what lets the answer be an honest "unknown" rather than a lost payment. This is also the pedagogically correct shape: the nine-second budget constrains *when you must answer*, not *whether the work is durable*.

**Why claim before delivering inline.** The background processor and the inline path would otherwise both deliver the same row. CoreBank's dedupe would absorb the duplicate, but the system would be relying on the receiver to cover a race the sender created. Claiming through the existing kernel path keeps exactly-once a property of the design rather than of the receiver's forgiveness.

**Why a budget timeout answers `202` and not `504`.** After a timeout the payment may have executed. `504` invites the caller to retry as if nothing happened; `202` with `Pending` states the truth — accepted, outcome not yet known, resolvable through the existing status endpoint. This is the ambiguity the talk is about, and the API should model it rather than hide it.

**Why CoreBank keeps its inbox.** Inline execution is a mode, not a migration. The inbox row is still the dedupe identity and still the fallback queue when inline execution fails, so the four-store drain semantics, the assertion API and the k6 gate need no changes. A later story may make the inbox optional for the instant rail; it should not be attempted here.

**Scope note.** A channel or web front-end, removal of any existing store, and changes to `LoadTestSupport` or `k6` are explicitly out of scope. This story is additive by construction so that it can land without re-greening the acceptance harness.

## Verification

**Commands:**
- `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankDemo.PaymentsAPI.Tests.csproj`
- `dotnet test tests/CoreBankDemo.CoreBankAPI.Tests/CoreBankDemo.CoreBankAPI.Tests.csproj`
- `dotnet test tests/CoreBankDemo.ServiceDefaults.Tests/CoreBankDemo.ServiceDefaults.Tests.csproj`
- `dotnet test tests/CoreBankDemo.Persistence.IntegrationTests/CoreBankDemo.Persistence.IntegrationTests.csproj`
- `dotnet test CoreBankDemo.Rebuild.slnf`
- k6 acceptance gate, unchanged, via the load-test AppHost

## Spec Change Log

### Review loop 1 (2026-09-03)

Two review findings, both confirmed against the running code before escalating, triggered this loop:

1. **Duplicate replay of a business rejection reported `Completed`.** `InstantPaymentForwardingHandler.ForwardAsync` calls `MarkAsCompletedAsync` for both a settled and a rejected outcome (per AD-11, `OutboxMessage.Status` is transport-state-only and never distinguishes the two), and the wire `Failed` status for a fresh rejection was synthesized ad hoc from `forward.Outcome`, never persisted. A same-idempotency-key resubmission of a rejected instant payment therefore replayed as `200 OK` / `Completed` — reporting a failed payment as successful. Resolution (human-selected): persist CoreBank's actual response on the Payments outbox row (new `ResponsePayload` column, mirroring CoreBank's own `InboxMessage.ResponsePayload`), and derive the replay's wire status from it. See Code Map additions above.
2. **CoreBank inline execution could race the background processor into a double ledger execution.** The background `InboxProcessorBase` acquires a partition-level distributed lock before claiming and processing rows; the new inline path (`TransactionIntakeHandler.TryExecuteInlineAsync`) called the shared `TransactionExecutionHandler` directly, with no lock and no row-level claim, and `CoreBankDbContext` never configured `InboxMessage.Status` as an EF concurrency token (only `PaymentsDbContext`'s entities had that). Since `StoreIfNewAsync` commits — making the row externally visible — before the inline call's own transaction opens, a background poll tick landing on the same row could execute the same ledger mutation twice. Resolution (human-selected): mirror the Payments-side `TryClaimByIdAsync` pattern for CoreBank's `InboxMessage` (row-level claim before inline execution, plus configuring the same concurrency token CoreBankDbContext was missing).

**KEEP instructions** (verified correct in loop 0, must survive re-derivation unchanged):
- The overall `InstantPaymentForwardingHandler` structure: claim-then-budgeted-retry-loop, per-attempt timeout, release-claim-on-exhaustion via `MarkAsFailedWithRetryAsync`, and the fresh-request outcome mapping in `PaymentsController.ToStoredResultAsync`/`ToInstantResponse` — all confirmed correct by three independent review passes and the full test suite plus a live k6 acceptance-gate run (100/100 transactions, `allPassed: true`).
- `IOutboxMessageStore<OutboxMessage>.TryClaimByIdAsync` / `MessageRepositoryBase.TryClaimByIdAsync` (the Payments-side single-row claim) — correct as implemented; CoreBank's new claim should mirror it, not replace it.
- CoreBank's inline execution reusing `TransactionExecutionHandler` unchanged inside its existing atomic commit (AD-5) — confirmed correct; only the *entry* into that handler needs a claim guard, not the handler itself.
- `Payments:InstantRail` options validation, the `Scheme` closed-set validation, the two new metrics (`payment.scheme`, `payment.instant.duration`), and `ADR-018`'s core narrative — all confirmed correct; ADR-018 needs only an addendum for the two fixes above, not a rewrite.
- Every existing passing test outside the narrow duplicate-replay and inline-claim paths — do not regress them while applying the fix.

### Review loop 2 (2026-09-03)

A second review pass over loop 1's fix surfaced four well-precedented, unambiguous patches (no human input required — applied directly):

1. **Permanently-`Failed` instant duplicate reported as `Pending` forever.** Loop 1's `ToDuplicateResult` normalized every non-`Completed` status to the wire word `Pending`, including a row that had permanently failed (`MarkAsFailedWithRetryAsync`'s terminal transition, retries exhausted) — masking a given-up delivery as still in flight. Fixed: a terminal `Failed` row now replays `202`/`Failed`; only `Pending`/`Processing` normalize to `Pending`.
2. **Replayed `ProcessedAt` was wrong.** `ToDeliveredResponse` sourced `ProcessedAt` from `snapshot.CreatedAt` instead of the real settlement time already present in the deserialized `ResponsePayload`. Fixed: `ResolveDeliveredResponse` deserializes once and returns both `Status` and `ProcessedAt` from the same value.
3. **Post-forward completion-persistence failure misclassified as a transport failure (the significant one).** `InstantPaymentForwardingHandler.ForwardAsync`'s per-attempt loop wrapped both the CoreBank forward call and the subsequent `MarkAsCompletedAsync` call in one try/catch, so a persistence failure *after* CoreBank had already committed was treated as retryable — re-invoking the forwarder and, on exhaustion, calling `MarkAsFailedWithRetryAsync`, flipping an already-paid transaction back to `Pending` and reporting `202` for a payment that had actually succeeded. This is the exact defect class `OutboxProcessorBase.ProcessMessageAsync` was already hardened against elsewhere in this codebase, with its own docstring and regression test. Fixed: split into two separate try/catch scopes mirroring that existing pattern; the caller now always receives the truthful outcome CoreBank already confirmed.
4. **Defensive validation + doc correction.** Added a startup check that `BudgetMilliseconds` stays under half of `MessageConstants.Defaults.ProcessingTimeout`, so a misconfigured budget can never reach the background processor's stale-claim reclaim window (which would reopen a double-execution race by a different mechanism). Corrected `ADR-018`'s addendum, which incorrectly called the `ResponsePayload` column "a schema change (new EF migration)" — this repository has no EF migrations; schema is provisioned via `EnsureCreatedAsync()`.

**KEEP instructions** (verified correct through loop 2, must survive any further changes unchanged): everything from loop 1's KEEP list, plus the loop-1 fixes themselves (`OutboxMessage.ResponsePayload` persistence, CoreBank's `TryClaimByIdAsync`-based inline claim, the `InboxMessage` concurrency token) — all independently re-verified via direct code inspection, the full test suite (940 tests, 0 failures, all coverage gates met), and two full live k6 acceptance-gate runs (100/100 transactions, `allPassed: true` both times) after loop 1 and again after loop 2.

## Suggested Review Order

**Entry point: the instant-rail decision**

- Where a payment opts into the budgeted inline forward instead of the standard 202 path.
  [`PaymentsController.cs:76`](../../../CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs#L76)

**Correctness fixes from review (read these first — they're where the real bugs were)**

- Two separate try/catch scopes so a post-commit persistence failure is never misclassified as a delivery failure — mirrors `OutboxProcessorBase`'s own historical fix for this exact defect class.
  [`InstantPaymentForwardingHandler.cs:91`](../../../CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs#L91)

- Duplicate replay now derives the true settled/rejected outcome from the persisted response instead of the transport-only kernel status column.
  [`PaymentsController.cs:115`](../../../CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs#L115)

- Single deserialization reused for both replayed `Status` and `ProcessedAt`, so they can never disagree.
  [`PaymentsController.cs:197`](../../../CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs#L197)

- The response payload is persisted on every completed delivery — inline and background alike — never an instant-only special case.
  [`HttpForwardOutboxDeliveryStrategy.cs:147`](../../../CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs#L147)

**CoreBank inline execution safety (the double-ledger-execution race)**

- Row-level claim before inline execution, so a concurrent background batch-claim can never win the same row.
  [`TransactionIntakeHandler.cs:273`](../../../CoreBankDemo.CoreBankAPI/Inbox/TransactionIntakeHandler.cs#L273)

- The generic single-row claim both services now share, guarded by the same optimistic-concurrency transition as the batch claim.
  [`MessageRepositoryBase.cs:490`](../../../CoreBankDemo.Messaging/MessageRepositoryBase.cs#L490)

- The concurrency token CoreBank's `InboxMessage` was missing (Payments' entities already had it) — this is what makes the claim above actually race-safe.
  [`CoreBankDbContext.cs:42`](../../../CoreBankDemo.CoreBankAPI/CoreBankDbContext.cs#L42)

**Budget and claim loop**

- Can any path exceed the configured budget or leave a claim held? Check the per-attempt timeout math and every exit branch.
  [`InstantPaymentForwardingHandler.cs:52`](../../../CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs#L52)

- Startup validation, including the loop-2 addition guarding against the background processor's stale-claim reclaim window.
  [`InstantPaymentRailServiceCollectionExtensions.cs:32`](../../../CoreBankDemo.PaymentsAPI/InstantPaymentRailServiceCollectionExtensions.cs#L32)

**Standard-rail regression check**

- Is the untouched standard path (`scheme` absent) provably byte-identical? Compare against `ToAcceptedResult`/`ToResponse`.
  [`PaymentsController.cs:142`](../../../CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs#L142)

**Contract and design record**

- The additive contract change and both fix addenda, recorded before/alongside the code that makes them.
  [`ADR-018-instant-payment-rail.md`](../../adr/ADR-018-instant-payment-rail.md)

**Peripherals**

- The closed-set `scheme` field and its validation.
  [`PaymentRequest.cs:38`](../../../CoreBankDemo.PaymentsAPI/Models/PaymentRequest.cs#L38)

- New `InlineCompleted` outcome mapped to `200` on the CoreBank side.
  [`TransactionIntakeHandler.cs:13`](../../../CoreBankDemo.CoreBankAPI/Inbox/TransactionIntakeHandler.cs#L13)
