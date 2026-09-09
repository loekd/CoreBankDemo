---
title: 'DemoRunner Operations workspace: stage-focus layout and Cancel payment'
type: 'feature'
created: '2026-09-09'
status: 'in-review'
baseline_commit: '66a81906f1abc8e4fed2fbeeac96fc80073642c1'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/EXPERIENCE.md'
  - '{project-root}/docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/DESIGN.md'
  - '{project-root}/docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/wireframes/operations-stage-focus.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The Operations workspace is the screen used on stage, and it spends 10 of 30 rows on shell chrome and 16 on controls, leaving 4 for the payments it exists to show. It offers no way to withdraw a running instant payment even though CoreBank has supported cancellation since `73ccbb1`, and it asks the operator for a currency that is always `EUR`.

**Approach:** Rebuild Operations as the approved Stage-focus layout — a two-line compose bar, one large focus card for the selected payment carrying that payment's own action, and a STILL OPEN strip that renders only while more than one payment is open — remove the three-row bottom band from all five workspaces, narrow the navigation rail to 16 columns, and add a **Cancel payment** action that calls CoreBank's existing `POST /api/transactions/cancel`. Both UX spines are the contract; they win over any wireframe.

## Boundaries & Constraints

**Always:**
- Both spines win on conflict with the wireframes and with this spec. Where they disagree with each other, `EXPERIENCE.md` owns behaviour and `DESIGN.md` owns tokens.
- Cancel adds **no endpoint to any banking service**. The console calls `POST /api/transactions/cancel` on CoreBankAPI, reached only through a new `KnownEndpoints` constant plus an `EndpointResolver` case (ADR-015 forbids operator-supplied URLs).
- The console never synthesises an outcome. A cancel resolves a payment only on the bank's own answer; a cancel that fails, times out, or returns something unrecognised leaves the payment exactly where it was.
- Removing the bottom band is **conditional** on every refusal the console produces itself being written to Evidence *before* its announcement is drawn. This is a requirement, not a nicety.
- State is always symbol + word (+ colour), never colour alone. State words are never abbreviated.
- Keyboard parity: every action reachable by mouse has a keyboard path. `R` (refresh) and `Q` (quit) currently exist only on the removed `StatusBar` and must survive.
- ADR-015 holds: no project reference to any banking project, no store or broker access.

**Ask First:**
- Any change to a banking project (`CoreBankDemo.CoreBankAPI`, `CoreBankDemo.PaymentsAPI`, `CoreBankDemo.ServiceDefaults`). This story is DemoRunner-only.
- Dropping or weakening an existing DemoRunner test rather than updating it to the new layout.

**Never:**
- No confirmation modal on Cancel payment (it destroys no state — argued in `EXPERIENCE.md` Interaction Primitives).
- Cancel is **not** lock-exempt and must not wear the `LockExemptScheme` teal outline. The burst Cancel and the Faults controls remain the only exemptions.
- No currency field, no currency validation in the console; always send `EUR`.
- No session history in Operations. The STILL OPEN strip is a work queue, not a log.
- No re-sorting, scrolling, or focus movement caused by an arriving event.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Cancel accepted | Card holds an outstanding payment; `POST cancel` → `200` body `Status: "Cancelled"` | Row resolves to `Cancelled`, leaves the strip; card reads `⊘ CANCELLED`, closing block "withdrawn before execution · no money moved" + "safe to retry with a new key"; evidence recorded | N/A |
| Cancel too late | `200` body `Status: "Completed"` (or `"Failed"`) | Row resolves to `Settled` (or `Rejected`); card carries "too late to cancel — the bank had already executed it" above the legs | N/A |
| Cancel refused, non-terminal | `409` body `Status: "Processing"` or `"Pending"` | Payment unchanged and still open; action slot returns to **Cancel payment**; announcement prints `cancel refused (409) — the bank reports status Processing` | Announcement + evidence, no state change |
| Cancel refused, terminal | `409` body `Status: "Failed"` | Row resolves to `Rejected` and leaves the strip — a status the bank stated about its own row is proof | N/A |
| Cancel transport failure | Timeout, connection failure, non-200/409, or unparsable body | Payment left exactly as it was; slot returns to **Cancel payment**; announcement prints the exact status or transport failure | Evidence record, `Succeeded: false` |
| Cancel with no id | Omitted-mode submission with no answer yet | Action renders in the slot, disabled, in `text-muted`, with the reason stated on the card | Never hidden, never empty slot |
| Strip collapse | 0 or 1 payment open | Strip and its captioned rule render **nothing at all**; feed status moves to the foot of the card region | N/A |
| Strip visible | 2+ payments open | One line per open payment, identifiers truncated to last four digits, feed status on the rule's right end | Surplus beyond available rows stated as `+N more open`, never dropped |
| Submit refused | No topology, or validation failure | Evidence record written **first**, then the transient announcement | Both, in that order |
| Burst running | Burst in flight or holding its result | Takeover replaces compose bar, card and strip; carries the feed statement on its rule | Feed loss restates `still moving` as `unknown` |

</frozen-after-approval>

## Code Map

**Cancel path (new)**
- `CoreBankDemo.DemoRunner/Application/KnownOperatorSurface.cs:86-97` — `KnownEndpoints` constants; add `corebank.transactions.cancel`. `EndpointResolverTests.cs:50` asserts every compiled constant resolves.
- `CoreBankDemo.DemoRunner/Infrastructure/EndpointResolver.cs:13` `CoreBankApiBaseUrl = http://127.0.0.1:5032` (profile-independent); `:36-53` `EndpointFor` — add the POST case.
- `CoreBankDemo.DemoRunner/Application/Ports/IPaymentGateway.cs:5` — three methods today (`SubmitAsync`, `QueryOutcomeAsync`, `InspectAsync`); add `CancelAsync`.
- `CoreBankDemo.DemoRunner/Infrastructure/HttpPaymentGateway.cs:14-83` `SubmitAsync` is the POST-with-body template (PascalCase wire fields, `Stopwatch` timing); `:97-146` `SendInspectionAsync` has **no body parameter**; `:212-219` `MapOutcome` reads the body's status word and ignores the HTTP code; `:223-246` `ParsePaymentResponse`.
- `CoreBankDemo.CoreBankAPI/Controllers/TransactionsController.cs:119-139` — `POST /api/transactions/cancel`, body `TransactionRequest`, `200` (Cancelled or committed) / `409` (current status) / `400`. Read-only reference.
- `CoreBankDemo.CoreBankAPI/Models/TransactionRequest.cs` — the five required fields; all five live on `TrackedPayment`. Read-only reference.
- `CoreBankDemo.CoreBankAPI/Inbox/TransactionCancellationHandler.cs:14-41` — the four outcomes behind those codes. Read-only reference.

**Controller and models**
- `CoreBankDemo.DemoRunner/Application/OperatorModels.cs:52` `MutationKind` (add `CancelPayment`); `:106` `PaymentTrackingState` (the seven card states); `:314-345` `TrackedPayment` holds Rail/Amount/Currency/From/To/TransactionId; `:436-508` `OperatorConsoleState` (`SelectedEvidence` is the precedent for a selected-payment field; there is none today).
- `CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs:194-204` `SelectWorkspace`/`SelectEvidence` — mirror for `SelectPayment`. `:1507-1575` `SubmitPaymentInternalAsync`. `:2059-2189` `TrackSubmittedPayment` (in-place update, 504 promotion at `:2096-2107`). `:1929-2025` `ResolveTrackedPayment` — Cancelled branch at `:1964-1996`; a console-fired cancel arrives back through the feed and lands here. `:2655-2693` `TryBeginMutation`/`EndMutation`. `:2701-2787` the two `AddEvidence` overloads.
- **Silent refusals to fix** (return without evidence): `:738`, `:749`, `:754`, `:779`, `:805`, `:810`, `:815`, `:821`, `:1515`, `:1521`, `:1526`.

**Terminal layer**
- `CoreBankDemo.DemoRunner/Terminal/MainWindow.cs:18` `RailWidthPreferred = 22` → 16; `:50` `OperationsChromeRows = 10` → 7; `:77-78` `Dim.Fill(3)` → `Dim.Fill()`; `:79-80` `_statusLine`/`_messageLine`; `:94` `_currency`; `:101` `_outcomeKey`; `:256-266` `StatusBar` (only home of `R`/`Q`); `:294-406` `BuildOperationsView`; `:1117-1201` `Render`; `:1236-1267` `BuildPaymentLines`; `:1314-1327` `RenderMessageLine`; `:1329-1349` `ApplyResponsiveLayout`; `:1364-1408` `ApplyOperationsRows`; `:1445-1488` `OnKeyDown`; `:1667-1679` `ShowMessage` (~20 call sites — becomes the transient announcement, not deleted); `:1858-1889` nested `ListBinding` (preserves selection and scroll offset — reuse for the strip).
- `CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs:91` `PaymentRowViewModel`; `:101-126` `OperatorPresentationModel` (`EvidenceStrip:107` and `MutationStatus:109` become dead); `:136-249` `Build`; `:277-356` `BuildPaymentRow` (the state switch); `:403` `ElapsedText`; `:256` `FeedStatusLine`.
- `CoreBankDemo.DemoRunner/Terminal/OperatorTheme.cs:19-32` — six scheme keys: `BaseScheme`, `RailScheme`, `ActionScheme`, `DestructiveScheme`, `OverlayScheme`, `LockExemptScheme`. Cancel takes none of the last three.

**Tests**
- `tests/CoreBankDemo.DemoRunner.Tests/Fakes/OperatorHarness.cs:224-305` `FakePaymentGateway` — a new port method forces an edit here; `:591` `PushCancelled` already exists.
- `tests/.../Terminal/MainWindowTests.cs` — `:54` rail width, `:95` currency field, `:387-388` message/status lines, `:646-717` payment-list layout theories, `:699-717` Omitted note.
- `tests/.../Terminal/PresentationModelBuilderTests.cs`, `tests/.../Application/OperatorConsoleControllerTests.cs:569-933` (cancellation semantics), `:998` (mutation lock), `:1060` (burst Cancel is the sole exception), `tests/.../Infrastructure/HttpPaymentGatewayTests.cs:282-311` (504 mapping), `tests/.../Infrastructure/EndpointResolverTests.cs:50`.
- Coverage allow-list in `CoreBankDemo.DemoRunner.Tests.csproj` includes all of `Application.*` plus `EndpointResolver*` and `HttpPaymentGateway*`.

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.DemoRunner/Application/KnownOperatorSurface.cs` + `Infrastructure/EndpointResolver.cs` -- add the `corebank.transactions.cancel` constant and its POST resolver case -- the console reaches CoreBank only through allow-listed ids.
- [x] `CoreBankDemo.DemoRunner/Application/OperatorModels.cs` -- add `PaymentCancellation` (the five request fields), `PaymentCancellationResult`, `PaymentCancelOutcome { Cancelled, AlreadyCommitted, Refused, TransportFailure }`, `MutationKind.CancelPayment`, and `OperatorConsoleState.SelectedPayment` -- the card needs a selected payment and cancel needs a typed answer.
- [x] `CoreBankDemo.DemoRunner/Application/Ports/IPaymentGateway.cs` + `Infrastructure/HttpPaymentGateway.cs` -- add `CancelAsync`; POST the `TransactionRequest` body and map `200`+`Cancelled`, `200`+committed, `409`+status, and everything else per the I/O matrix -- the body carries the business meaning, never the code alone.
- [x] `CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs` -- add `SelectPayment` and `CancelPaymentAsync`; take the mutation lock; resolve or leave the tracked row per the I/O matrix; make a new submission take the selection; and record evidence at every refusal site listed in the Code Map **before** any announcement -- the band's removal is conditional on it.
- [x] `CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs` -- add `FocusCardViewModel` and `StillOpenRowViewModel`, derive which payment the card holds (explicit selection → the single open payment → the most recently resolved → placeholder), expose `ShowStillOpen` (open count > 1) and the announcement; delete `EvidenceStrip` and `MutationStatus` -- selection drives the card and nothing else does.
- [x] `CoreBankDemo.DemoRunner/Terminal/MainWindow.cs` -- rebuild `BuildOperationsView`/`ApplyOperationsRows` as compose bar + focus card + strip; drop the currency and outcome-key fields; add the card's action button (Cancel payment / Look up outcome / Resend same key in one slot); add the burst takeover; remove `_statusLine`, `_messageLine` and the `StatusBar`, moving `R`/`Q` into `OnKeyDown`; set the rail to 16 and chrome rows to 7; re-point `ShowMessage` at the transient announcement row -- this is the screen the talk is given from.
- [x] `tests/CoreBankDemo.DemoRunner.Tests/Fakes/OperatorHarness.cs` -- queue-drive `FakePaymentGateway.CancelAsync` in the same shape as `Queue`/`QueueInspections` -- every controller test needs it.
- [x] `tests/CoreBankDemo.DemoRunner.Tests/**` -- unit-test every I/O matrix row, update the layout/currency/message-line tests to the new workspace, and keep the ≥90% line-coverage gate green.

**Acceptance Criteria:**
- Given a 100×30 terminal, when Operations renders, then shell chrome occupies 7 rows and the payment area 20.
- Given the console has submitted an instant payment and no answer has arrived, when the operator presses the card's action, then a cancel is dispatched immediately with no confirmation modal, the slot reads `Cancelling — Ns`, and the payment's own state, clock and strip line are unchanged until the bank answers.
- Given a second activation of Cancel arrives while one is in flight, when it is handled, then exactly one cancel request is dispatched.
- Given any mutating action is in flight elsewhere, when Operations renders, then Cancel payment is dimmed like every other mutating control and does not wear the lock-exempt outline.
- Given the console refuses a submission itself, when the announcement is drawn, then an Evidence record for that refusal already exists.
- Given a payment resolves while the operator has selected a different open payment, when the model rebuilds, then the card still holds the operator's selection and no list re-sorts or scrolls.
- Given a terminal at the 80×24 floor, when Operations renders, then the compose bar keeps both captions, the card keeps its state word, clock and action, and the feed statement is present in one of its two forms.
- Given the console sends any payment, when the request is built, then `Currency` is `EUR` and no currency input exists on screen.

## Design Notes

The transaction id **is** the idempotency key (`PaymentStorageHandler` sets both from `idempotencyKey ?? Guid.NewGuid()`), which is what makes Cancel reachable for the whole of an instant payment's 9000 ms budget rather than only after it resolves. Omitted mode is the single exception, and that is the cost its existing "not retry-safe" label already names.

The seven `PaymentTrackingState` values map to the card's state words; `Cancelled` renders two of them — `⊘ CANCELLED` for an operator-fired or broadcast cancellation, and `⊘ CANCELLED BY THE RAIL` when the submission itself answered `504 Cancelled`, which is the one place the console says *who* withdrew a payment. The state column is 34 cells, sized to the longer of the two.

A console-fired cancel also comes back through the outcome feed as `com.corebank.transaction.cancelled` and lands in `ResolveTrackedPayment`'s existing Cancelled branch. The response is what resolves a cancellation this console requested; the broadcast confirms it. CoreBank publishes nothing for a replayed cancellation, so the card must not wait for one.

## Verification

**Commands:**
- `dotnet build CoreBankDemo.UnitTests.slnf` -- expected: no errors, no new warnings.
- `dotnet test CoreBankDemo.UnitTests.slnf` -- expected: all tests pass.
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: both tiers pass and the ≥90% line-coverage gate holds.

**Manual checks (if no CLI):**
- Render Operations at 100×30 and at 80×24 through the existing `ResizeForTest`/`RenderForTest` seams and confirm the row budget and that nothing is hidden at either size.
