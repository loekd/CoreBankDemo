# Operations gets three modes: Single, Burst, Fetch

> **Status:** Draft
> **Kind:** design spec
> **Original date:** 2026-10-08
> **Related:** [ADR-015](../../adr/ADR-015-presentation-safe-terminal-demo-console.md); [ADR-027](../../adr/ADR-027-payment-status-local-projection.md); [payment status GET](2026-10-07-payment-status-get-design.md); [UX experience](2026-09-03-demorunner-ux-experience-design.md); [Operations stage focus](2026-09-09-demorunner-operations-stage-focus-design.md)

## Intent

**Problem:** The Operations compose bar switches between a single payment and a burst with one
toggle button whose caption names the mode it switches *to* (`Burst mode` / `Single mode`). On
stage that reads backwards and stays confusing. The bar also shows controls the active mode
ignores: the idempotency chip is drawn in burst mode, but a burst never reads it. And the console
cannot call PaymentsAPI's new `GET /api/payments/{transactionId}` (ADR-027) at all; its only
lookup, the card's **Look up outcome**, asks CoreBank.

**Approach:** Replace the toggle with a horizontal three-way `OptionSelector` at the top of the
compose area — `Single`, `Burst`, `Fetch` — and give each mode its own dedicated control bar with
its own action button. `Fetch` calls `GET /api/payments/{transactionId}` for a typed id (prefilled
from the card) and shows the answer on a persistent result line under its bar, plus an Evidence
record. The card's Look up outcome keeps asking CoreBank, so the console can show both views: on
stage, PaymentsAPI's `Pending` beside CoreBank's settled outcome is ADR-027's "late, never wrong".

**Success:** an operator can tell at a glance which mode is active and what its button does; a
Fetch press always produces visible feedback — in flight, answered, not found, or failed — even when
the answer is identical to the previous one.

## Boundaries & Constraints

**Always:**
- The focus card's state stays driven by the outcome feed only. A Fetch answer never opens,
  closes or changes a tracked payment.
- The console reaches the endpoint only through an allow-listed endpoint id (ADR-015); the operator
  types an id, never a URL.
- Every Fetch writes an Evidence record with the full request and response.
- The layout fits the 80×24 floor (71 usable columns) with every caption drawn.
- Verified against the drawn screen buffer, not view geometry.

**Ask First:**
- Any change to PaymentsAPI or CoreBank (none is needed).
- Changing the card's Look up outcome target, label or key binding (`O`).

**Never:**
- A Fetch answer as proof of an outcome on the card or in the still-open strip.
- A tab control (`Tabs`): its headers cost rows the 80×24 floor does not have, and its selected
  tab follows focus, which fights the compose bar's literal Tab order.
- Fetch behind the single-action-in-flight lock: it is read-only, like Look up outcome.

## Layout

The selector takes row 0 of the compose area. Each mode's bar follows on rows 1–2. Every mode is
three rows tall, so switching modes never moves the card (with the default Generated key mode; the
Supplied/Omitted line beneath Single adds its row exactly as today). The compose rule follows as today.

```
 ◉ Single  ○ Burst  ○ Fetch
 From [NL91ABNA0417164300] → To [NL20INGB0001234567]   ⟦► Submit ◄⟧
 Amount [1.00    ]  ⟦ Rail ‹ standard ›⟧  ⟦ Key ‹ Generated ›⟧

 ○ Single  ◉ Burst  ○ Fetch
 From [NL91ABNA0417164300] → To [NL20INGB0001234567]   ⟦► Send burst ◄⟧
 Amount [1.00    ]  ⟦ Rail ‹ standard ›⟧  Count [200 ]  at once [10  ]

 ○ Single  ○ Burst  ◉ Fetch
 Payment id [regular-apphost-demo-payment-v1      ]     ⟦► Fetch ◄⟧
 ✓ 200  Pending · 1.00 EUR · since 14:02:09      fetched 14:02:11 · 38 ms
```

- **Single:** today's single-payment bar, unchanged: From, To, Submit; Amount, Rail chip, Key chip.
  The Supplied-key / Omitted-warning line still appears beneath it in those key modes, as today.
- **Burst:** From, To, **Send burst**; Amount, Rail chip, **Count**, **at once**. No Key chip: a
  burst never reads it. Count and at-once fields narrow to 6 cells so the second line fits 71
  columns (Amount 1–14, Rail chip 17–37, Count 40–51, at once 53–66).
- **Fetch:** **Payment id** field and **Fetch** on row 1; the result line on row 2. The field is
  wide enough for a GUID (36 characters) at 71 columns, and scrolls for longer ids (up to 100).
- From, To, Amount and Rail are one set of values shared by Single and Burst: switching modes keeps
  what was typed.
- The selector's glyphs are whatever `OptionSelector` draws in the operator theme; the sketch's
  `◉`/`○` are illustrative, and tests assert the three labels and which one is selected.
- The selector replaces `Burst mode`/`Single mode` entirely; Submit no longer changes caption or
  meaning. Each bar's button does one thing.
- The selector is display state: switching modes is never locked by an in-flight action, and does
  not cancel one. While a burst runs, the burst takeover still replaces the whole workspace, as today.

## Fetch

**Id prefill.** When Fetch is selected and the field is empty or still holds the id it was last
prefilled with, it is filled with the card's payment id. An id the operator typed is never
overwritten. With no payment on the card, the field is left as it is.

**Call.** Fetch sends `GET /api/payments/{id}` through a new endpoint id `payments.status`
(`EndpointResolver`, profile-aware: 5294 Regular, 5295 LoadTests), the id trimmed and escaped with
`Uri.EscapeDataString`, like `corebank.transactions.status`. Refusals, without a call and with an
Evidence record, like Look up outcome: no topology started or attached; empty id.

**Result line** — persistent until the next Fetch, so a repeated identical answer still visibly
changes its stamp. The right end always carries `fetched HH:mm:ss · <n> ms`: the press time in the
console's own clock format (`OutcomeFeedNarrative.Clock`), and the duration in whole milliseconds as
the Evidence pane prints it.

| State | Left part | Tone |
|---|---|---|
| In flight | `~ GET /api/payments/<id> …`; Fetch reads `Fetching — 0s` (whole seconds, like the card's `Cancelling — 3s`) and is disabled | neutral |
| `200` with a readable `PaymentResponse` | `✓ 200  <Status> · <amount> <currency> · <since\|at> HH:mm:ss` — `since` for `Pending`, `at` otherwise; the time is the body's `processedAt` | accent (teal) |
| `404` | `○ 404  no payment with this id` | neutral |
| `200` with an unreadable body | `✗ 200  unreadable response body` | failure |
| Any other status | `✗ <code>  unexpected answer from PaymentsAPI` | failure |
| Timeout or no connection | `✗ PaymentsAPI unreachable — <reason>` | failure |
| Refused (no topology, empty id) | `✗ <refusal reason>`, with no request sent | failure |

No new colour: the theme has no success green, so `✓` takes the teal accent and `✗` the existing
failure token. A failure also stays out of the transient announcement row; the result line and the
Evidence record carry it.

**Evidence.** Kind `OutcomeQuery`, method `GET`, target the endpoint id, the status code, the
duration, the body or error summary, and the exchange. Titles `Payment status fetched` (any HTTP
answer) and `Payment status fetch failed` (no HTTP answer); a refusal follows the console's refusal
convention, `Payment status fetch refused`. The record counts as succeeded for a `2xx` and for a
`404` — an unknown id is an answer, not a failure. It names the creditor account when the id is a
payment the console tracks, as the outcome query does.

**Typed id.** The UX experience spec retired a standing typed **Outcome key** field in favour of the
card's untyped lookup. Fetch deliberately brings back a typed field, scoped to its own mode and
prefilled from the card: it must reach any payment, including ones sent by k6 or an `.http` file.
The card's Look up outcome stays untyped.

## Keyboard

The selector sits first in the Operations Tab order, ahead of the active bar's fields. Arrow keys
move within it, as `OptionSelector` provides. Global keys are unchanged: `1`–`5`, `0`, `R`, `Q`,
`O` (card lookup, still CoreBank) and `T` reach the window only outside a text field, as today. No
new global key.

## Code Map

DemoRunner only.

- **`Application/KnownOperatorSurface.cs`**: `KnownEndpoints.PaymentStatus = "payments.status"`.
- **`Infrastructure/EndpointResolver.cs`**: map it to `{PaymentsBaseUrl(profile)}/api/payments/{escaped id}`, `GET`.
- **`Application/Ports/IPaymentGateway.cs`, `Infrastructure/HttpPaymentGateway.cs`**:
  `FetchPaymentStatusAsync(profile, id, ct)` returning `InspectionResult` through the existing
  `SendInspectionAsync`.
- **`Application/OperatorConsoleController.cs`**: `FetchPaymentStatusAsync(id, ct)` — refusals,
  Evidence record, and the last fetch held in state (in flight, then its outcome) for the result
  line. No tracked-payment update.
- **`Application/OperatorModels.cs`**: the last-fetch record on `OperatorConsoleState`; parsing the
  `PaymentResponse` body (`status`, `amount`, `currency`, `processedAt`) for the line.
- **`Application/EvidenceTitles.cs`**: the two titles.
- **`Terminal/PresentationModel.cs`**: the result line's text and tone, and the Fetch button's
  caption/enabled state, from the last fetch and the clock.
- **`Terminal/MainWindow.cs`**: the selector, the three bars, the mode enum replacing
  `_burstSetupVisible`, the prefill rule, removing `_burstButton` and its captions; row ladder
  (`ComposeBarRows`, `ModeButtonRow`) recomputed so all three modes take three rows.

## Tests

xUnit + AwesomeAssertions + Moq; ≥90% line coverage holds.

- **Render (screen buffer, 80×24 and 100×30)**, replacing `TheModeButton_DrawsOnItsOwnLine_AndTheBurstFieldsShareIt`:
  each mode's three rows drawn whole — selector row, both bar rows with every caption and chip,
  `⟦► Submit ◄⟧` / `⟦► Send burst ◄⟧` / `⟦► Fetch ◄⟧`; Burst draws no Key chip; the card's first
  row is on the same screen row in all three modes.
- **Result line (drawn)**: one case per state in the table, including the right-end stamp, and a
  second identical `200` that changes only the stamp.
- **Presentation model**: line text and tone per state; Fetch caption while in flight.
- **Controller**: refusals record Evidence and send nothing; a `200`, a `404` and a transport failure
  each record the right title; a Fetch never changes `TrackedPayments`; a Fetch is accepted while
  another action is in flight.
- **Endpoint resolver**: Regular and LoadTests URLs; an id with `/` is sent as `%2F`; a missing id
  is refused.
- **Gateway**: a `200` body, a `404`, a timeout and a connection failure map to `InspectionResult`
  as for the existing inspection calls.
- **MainWindow**: prefill on selecting Fetch; a typed id is not overwritten; Single/Burst share the
  account and amount values; Submit always sends one payment and Send burst always sends a burst.
- Existing tests that toggle `BurstButton` move to the selector.

## Docs

- **`2026-09-03-demorunner-ux-experience-design.md`**: the **Compose bar** and **Burst control**
  rows describe the selector and the dedicated bars; a new **Payment status fetch** row; the
  **Outcome query** row notes it still asks CoreBank and that Fetch is the PaymentsAPI view; the
  80×24 floor row states the three-row modes.
- **`2026-09-03-demorunner-ux-design.md`**: the compose-bar component description, if it names the
  mode button.

## Acceptance

- At 80×24, each mode's bar is fully drawn and the card does not move when switching modes.
- Submit sends one payment and Send burst sends a burst; neither button changes caption.
- Fetch on a payment just submitted shows `✓ 200  Pending …`; after its `transaction.completed`
  event has been processed by PaymentsAPI, a second Fetch shows `✓ 200  Completed … at …` while the
  card's state was already settled by the feed.
- Fetch on an unknown id shows `○ 404`; with PaymentsAPI stopped, `✗ PaymentsAPI unreachable`.
- `dotnet test CoreBankDemo.UnitTests.slnf` passes with coverage at or above the gate.

## Out of scope

- Any change to the card's Look up outcome, or showing both lookups in one place.
- Polling: Fetch is one call per press.
- Fetching from the burst takeover.
