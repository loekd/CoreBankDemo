---
title: 'Adversarial review: DemoRunner UX spines — live-stage safety and recoverability'
type: 'review'
created: '2026-09-09'
supersedes: 'the 2026-09-03 stage-safety pass of this same file'
reviewed:
  - DESIGN.md
  - EXPERIENCE.md
  - .working/update-brief.md
  - wireframes/operations-stage-focus.md
  - .memlog.md (entries 80-107)
  - ../../../../../CoreBankDemo.CoreBankAPI/Controllers/TransactionsController.cs
  - ../../../../../CoreBankDemo.CoreBankAPI/Inbox/TransactionCancellationHandler.cs
  - ../../../../../CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs
  - ../../../../../CoreBankDemo.ServiceDefaults/CloudEventTypes/Constants.cs
  - ../../../../../CoreBankDemo.ServiceDefaults/CloudEventTypes/TransactionCancelledEvent.cs
---

# Adversarial review: live-stage safety and recoverability (Stage-focus pass)

**This file supersedes the earlier stage-safety pass dated 2026-09-03.** That pass reviewed the
pre-Stage-focus spines and closed all ten of its findings; this pass, dated **2026-09-09**, reviews
the Operations redesign, the removal of the bottom band from all five workspaces, the cancel
affordance, and the renamed burst counters. The prior pass's ten findings were re-checked and
remain resolved (see *Prior pass, re-checked* below); everything else here is new.

Lens: a presenter operating alone under time pressure while talking, in front of 250 people, with
no retry on visible failure. The governing principle under test is that the console must never
claim something the system has not proven. Scope is `DESIGN.md`/`EXPERIENCE.md` and the wireframes
they reference, not visual style. Where a finding turns on what the running system actually
answers, it was verified against the code rather than assumed.

## Overall verdict

**Fail — do not build the cancel affordance or the burst takeover from these spines as written.**
The Stage-focus layout is a genuine improvement and the cancel state model is more careful than
most of this file's peers, but four defects would let the console assert something untrue in front
of a room, and two of them sit on the exact beats the redesign was built for. The most serious is
not the transient announcement — that change is better defended than it looked — but the **burst
takeover, which replaces every surface that carries the feed statement and replaces none of it**,
and the **`504 Cancelled` answer the instant rail now returns, which appears nowhere in either
spine while three Key Flows narrate `202 Pending` in its place**.

Counts: **4 Critical, 7 High, 6 Medium, 2 Low**, plus 4 items I could not settle.

---

## Confirmed findings

### Critical

**C1 — The burst takeover carries no feed statement, so the console's most-watched screen cannot say whether anyone is listening.**

*Where:* `EXPERIENCE.md` Component Patterns, *Burst takeover* and *Feed status (inline)*;
`DESIGN.md` Components, *Feed status, inline* and *Burst takeover*.

*What the spine says:* the takeover "**replaces** the compose bar, the focus card and the STILL
OPEN strip and occupies the whole content area." And feed status "appears in exactly two places:
once at the foot of the Operations content area … right-aligned on the last row when the still-open
strip is collapsed, and carried on the right end of that strip's captioned rule when it is not."
Both of those places are precisely what the takeover deletes. The takeover's own body is specified
as "the two counter lines and the bar"; its captioned rule carries the run name and a clock, and
nothing else. Wireframes S8 and S9 confirm it: neither screen contains the words `listening since`.

*On-stage failure:* for the whole duration of a 200-payment burst — Flow 3's climax, Flow 4 step 6,
Flow 2 steps 1-5 — the screen shows `Settled 187 · still moving 13` with no statement anywhere that
the console is still hearing anything. `still moving` is only meaningful if it is. If the
subscription drops mid-burst the counter freezes at 13 and the screen keeps displaying a number
that has silently changed meaning from "13 payments the console is waiting to hear about" to "13
payments nobody is listening for." The transient announcement fires once and clears; the strip that
would have restated its rows as `Outcome unknown` is not on screen; the payments themselves are
"counted, never listed," so there are no rows to restate. Flow 3's failure branch claims the
opposite — "the console is incapable of leaving it standing once it stops listening" — and during a
takeover that claim has no mechanism behind it. The operator holds the room on a number that has
quietly stopped being true, which is the single failure this whole capability exists to prevent.

*Proposed fix:* in `EXPERIENCE.md` Component Patterns, *Burst takeover*, add: "The takeover carries
the region's feed statement on the right end of its captioned rule, beneath the run clock — it is
the one thing the takeover inherits from the surfaces it replaces, because `still moving` is a count
of payments the console is currently listening for and is unreadable without it. On feed loss the
**Settled** line itself changes: `still moving` leaves the screen at that instant and the line reads
`Settled 187 · rejected 0 · 13 outcomes unknown — the console stopped listening at 12:06:02`, so the
takeover is structurally incapable of displaying a wait nobody is performing, exactly as a payment
row is." Mirror the placement in `DESIGN.md` Components, *Feed status, inline* ("exactly two places"
becomes three, the third being the burst takeover's rule) and *Burst takeover*.

---

**C2 — `504 Cancelled` exists in the shipped system and in neither spine; three Key Flows narrate `202 Pending` for the payment the rail has already withdrawn.**

*Where:* `EXPERIENCE.md` Voice and Tone (status vocabulary), State Patterns (the payment states),
Flow 1 step 3, Flow 2 step 6, Flow 4 failure branch.

*What the spine says:* Flow 1 step 3 — the instant payment against a stopped dependency "shows a
distinct outcome: `202 Pending` (budget exhausted / transport failure, background delivery
continues)." Flow 2 step 6 — the instant payment into an 800-2000 ms latency band "cannot settle
inside its budget: the card sits at `~ AWAITING SETTLEMENT` with the clock climbing past four
seconds and its lifecycle line reading `submitted ──▶ 202 Pending ──▶ waiting for the bank`." Flow 4's
failure branch — "the focus card truthfully shows `202 Pending` instead of `200`." Searching both
files for `504` returns nothing.

*What the system does:* `CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs`,
`ToStoredResultAsync` — on the instant rail, `InstantDeliveryOutcome.Cancelled` (budget exhausted
*and* the command provably withdrawn) answers **`504` with a frozen body whose `Status` is
`Cancelled`**; only `Deferred` answers `202`. `ToCancelledResult` replays the same `504` for the
same key. So budget exhaustion has two possible answers and the spines describe only one of them —
the one that means "still open" — while the other means "provably dead, nothing executed, safe to
retry with a new key."

*On-stage failure:* two branches, both bad. If the console treats an unrecognised `504` as a
transport failure it renders red — dressing a cancellation as a failure, which check 4 of this
lens bans outright and which `DESIGN.md` argues against at length for the broadcast path. If it
treats the payment as unresolved it sits at `~ AWAITING SETTLEMENT` with a climbing clock and a
line reading `waiting for the bank`, for a payment the rail has already withdrawn — an unproven
"still waiting" claim contradicted by the response body the console is holding. Flow 2 step 6 is
the worse case: it is the beat where the operator narrates cancelling a hanging payment, and in the
shipped system that payment may have answered `504 Cancelled` at submission, before he touches the
card. He would be claiming to cause an outcome the system produced on its own.

*Proposed fix:* add a Voice and Tone row — `504 Cancelled — the rail's budget expired and the
payment was provably withdrawn; no money moved` opposite the Don't `504 Gateway Timeout` (a
transport word for a business outcome). Add a State Patterns row, *Payment: Cancelled by the rail
(`504 Cancelled`)*: "The instant rail exhausted its budget and CoreBank confirmed the command was
withdrawn before it executed, so the submission itself answers `504` with `Status: Cancelled`. The
card resolves immediately to `⊘ CANCELLED` — the same rendering as an operator-fired cancellation
and for the same reason, since it is the same outcome reached without the operator asking — and the
payment never enters the STILL OPEN strip. `504` is a *proven* outcome here and is never rendered
as a transport failure; the rail's own `202` remains the only budget-exhaustion answer that leaves a
payment open." Then correct Flow 1 step 3, Flow 2 step 6 and Flow 4's failure branch to name both
answers and say which is being narrated.

---

**C3 — The `409` branch prints "the bank is executing it right now" for an answer that also means terminally failed.**

*Where:* `EXPERIENCE.md` State Patterns, *Payment: Cancel refused — mid-execution (`409`)*;
`.working/update-brief.md` §5 table row 3.

*What the spine says:* "The bank is executing the payment at the moment the cancel lands and
refuses to take a position on it. The card **stays awaiting**, clock still running, and reads 'the
bank is executing it right now'."

*What the system does:* `TransactionCancellationOutcome.InFlight`, which is the only outcome mapped
to `Conflict()`, is documented in `TransactionCancellationHandler.cs` as: "The row cannot be
cancelled: it is `Processing` under a live claim, **terminally `Failed`**, or lost the claim race.
Maps to `409` carrying the current status." Three distinct situations, one of them terminal, and
the response body carries which one it is.

*On-stage failure:* the operator cancels a payment CoreBank has already terminally failed. The
console tells the room, in the console's own voice, that the bank is executing it right now; the
clock keeps climbing; the card keeps offering **Cancel payment**; and the payment stays in the
STILL OPEN strip for the rest of the session. That is a positive claim about the bank's internal
state that the console has not observed, contradicted by the very response it is rendering — the
console had the answer in hand and printed a sentence that disagreed with it. It is also
unretractable in practice: nothing will ever arrive to correct it, because the failure already
happened.

*Proposed fix:* replace the sentence and the state rule: "The bank refuses to take a position: the
`409` says only that the row cannot be withdrawn now, and its body carries the row's current status.
The console prints that status verbatim rather than a sentence about what the bank is doing —
`cancel refused (409) — status Processing` or `status Failed` — because 'the bank is executing it
right now' is an inference the console cannot make from a refusal. The card stays awaiting **only
while the returned status is non-terminal**; a terminal status in a `409` body is an outcome the
console has been told and is rendered as one, exactly as if it had arrived by broadcast. The action
slot keeps **Cancel payment** in the non-terminal case, because the refusal was about this instant
and not about the payment." Update the `.working/update-brief.md` table row to match.

---

**C4 — A submission whose response never arrives has no payment state anywhere, and the strip's collapse then reads as "nothing is happening."**

*Where:* `EXPERIENCE.md` Foundation (*Outcome-feedback boundary*, second boundary), Component
Patterns *Still-open strip*, State Patterns; `DESIGN.md` Components, *Still-open strip*
(`empty-height: 0row`).

*What the spine says:* "the console **correlates only by `TransactionId`**, which the submission's
own `202`/`200` response already returns." The strip "lists every payment with no proven outcome
yet." Its zero state "renders nothing at all — `empty-height: 0row`, rule included, no placeholder
row," and that is defended on the grounds that "the region above is never empty."

*On-stage failure:* the operator presses Submit under 40% injected errors — Flow 2 step 5's exact
conditions — and the response never comes back. The console has no `TransactionId`, so it has
nothing to correlate, nothing to put in the strip, nothing to select, and nothing for **Look up
outcome** to look up. The strip stays collapsed, the focus card keeps showing whatever resolved
before, the failure is announced once in a transient line and clears, and the screen returns to a
calm, settled-looking picture — while a payment that may have reached CoreBank and moved money is
outstanding and untracked. This is exactly the state the collapse rule was told to be safe against
("its absence can never be read as everything is fine"), and it is the one case the spine's own
defence does not cover: the card being non-empty says nothing about a payment the console lost.

*Proposed fix:* the console holds more than it thinks. In Generated and Supplied modes it knows the
idempotency key before it sends, and the **Outcome query** already accepts "its captured id **or
key**." Add a State Patterns row, *Payment: no response*: "A submission whose response never arrived
— transport failure, timeout, or a fault-injected error on the response path — is not an
un-submitted payment and is never treated as one. It enters the STILL OPEN strip immediately, keyed
by its idempotency key, as `○ Outcome unknown — no response; this payment may or may not exist`, and
it stays there until an **Outcome query** on that key resolves it. In Omitted mode there is no key
to hold, and the console says so in the same line rather than dropping the payment: `no response and
no key — this payment cannot be looked up`, which is the concrete cost the Omitted-mode warning
names before submission." Then amend the `DESIGN.md` zero-state justification: "the strip's absence
means *no payment is open*, never *nothing went wrong* — every payment the console has lost track
of is listed there, which is what makes the collapse safe."

---

### High

**H1 — The focus card's "exactly one closing block, never two" makes a contradicted outcome unrenderable on the one object the room reads.**

*Where:* `EXPERIENCE.md` Component Patterns, *Focus card*; State Patterns, *Contradicted outcome*;
`DESIGN.md` Components, *Focus card* (`foreground-settled`/`-rejected`/`-awaiting`/`-cancelled`/`-unknown`).

*What the spine says:* the card carries "exactly one closing block: the lifecycle in words while
unresolved, the balance legs once settled, or the `ErrorReason` once rejected. **Never two blocks at
once, never an invented fourth.**" Meanwhile the contradiction rule requires that "**both records
stay on screen, both stay labeled with their source and their time**." The card has five state
tokens and none of them is a contradiction; `foreground-contradiction` exists only on `event-row`,
which lives in Evidence.

*On-stage failure:* an instant-rail `200 Completed` followed by `com.corebank.transaction.failed` —
which the spine itself calls "the most valuable thing the feedback loop can surface" — arrives, and
the largest object on the screen can render only one of the two facts. The card shows a resolved
payment with one block, the room reads a clean outcome, and the disagreement is discoverable only by
switching workspaces. With the session history gone from Operations, the card is the whole of what
the room sees. A console that silently picks a side on its most-watched surface is doing exactly
what `Interaction Primitives` bans two sections later.

*Proposed fix:* name the contradiction as a fourth permitted closing block and a sixth card state:
"A contradicted payment is the one case where the card's closing block holds two records rather than
one, because a contradiction *is* two records: each printed with its source and its time (`HTTP
200 Completed 12:04:31` / `broadcast Failed 12:04:36`). Its state word is `✕ CONTRADICTED` and it is
never resolved to either side. This is the single exception to the one-block rule and it exists
because collapsing a contradiction into one block would be the silent tie-break this file bans." Add
a `foreground-contradiction` entry to `DESIGN.md`'s `focus-card` token block, reusing `state-failed`
as `event-row` already does.

---

**H2 — Cancel has no in-flight state and no answer for a reply that never arrives.**

*Where:* `EXPERIENCE.md` Component Patterns, *Cancel payment action*; Interaction Primitives,
*Cancelling a payment fires with no confirmation*.

*What the spine says:* "It fires immediately — no confirmation modal, no `Y`." "Its outcome is never
assumed: the console renders whichever of the bank's three answers it actually gets." Nothing states
what the card shows between the keypress and the answer, or what it shows if no answer ever comes.
Every other command in this console has a stated "still working, not hung" affordance — resource
rows ("Restarting — 4s"), the AppHost panel, the Load workflow's Wait phase, the fault chip's
`Applied — not yet observed in traffic` — all of them added by the *previous* stage-safety pass.
Cancel is the one action that skips the modal, so there is not even a modal as evidence it was
pressed.

*On-stage failure:* Flow 2 step 6 fires Cancel with an 800-2000 ms latency band in force and
possibly a 40% error rate. The operator presses it; the card correctly does not change (its state
must not move on an unproven cancel); nothing else changes either. Mid-sentence, with no artifact of
the keypress on screen, he presses it again. The second cancel is a **replay**, and per
`CloudEventTypes/Constants.cs` a replayed cancellation "publishes nothing (it was published once
already)" — so the second press cannot produce the event the card may be waiting for (see H3). Worse
in the other direction: if the cancel call itself dies in the proxy, the console has recorded a
request whose fate it does not know and has said nothing about it.

*Proposed fix:* add to *Cancel payment action*: "On dispatch the card's **action slot** — not its
state — re-states itself as `Cancelling — 3s` with the same elapsed-time readout every other
commanded transition carries, and the payment's own state, clock and strip line are untouched:
asking is not an outcome. Duplicate activation is debounced to one dispatch, as everywhere else. If
the call fails, times out, or answers anything the console does not recognise, the slot returns to
`Cancel payment`, the announcement prints the exact status or the exact transport failure, and the
payment is left exactly where it was — a cancel whose reply never arrived leaves the console
asserting neither success nor failure, and the request itself is recorded in Evidence so the
operator can see that it was made."

---

**H3 — Whether `⊘ CANCELLED` is proven by the `200` or by the broadcast is stated both ways, and one of the two readings hangs the card forever.**

*Where:* `EXPERIENCE.md` Component Patterns, *Cancel payment action*; State Patterns, *Payment:
Cancel accepted (`200 Cancelled`)*; Flow 2 step 6.

*What the spine says:* three statements, two answers. Component Patterns: the console "**waits for**
`com.corebank.transaction.cancelled` to resolve the payment exactly as a settlement resolves one."
State Patterns: "the bank withdrew the payment before it executed. **The card resolves to `⊘
CANCELLED`** with its final clock" — on the `200`, with no mention of waiting. Flow 2 step 6: "A
moment later `com.corebank.transaction.cancelled` arrives **and the card flips** to `⊘ CANCELLED`."

*On-stage failure:* under the waiting reading, the case CoreBank documents as publishing no event —
a *replayed* cancellation, and a cancellation that never left PaymentsAPI — leaves the card at `~
AWAITING SETTLEMENT` permanently while the console holds a `200 Cancelled` response in hand. That is
not hypothetical: it is Flow 2 step 6's own scenario the moment the instant rail has already
cancelled the payment on budget exhaustion (C2), because the operator's cancel is then the replay.
Under the resolving reading, the Component Patterns sentence is simply false and Flow 2's narration
misdescribes what the room is watching. Either way the implementer picks one and the spine endorses
the other.

*Proposed fix:* state one rule in all three places: "The bank's `200 Cancelled` **is** the proven
answer and resolves the card on arrival — it is the bank's own committed response about its own
row, not an inference from a status code, and it is the same class of proof as the instant rail's
`200 Completed`. The `com.corebank.transaction.cancelled` broadcast, when it comes, confirms it and
supplies `ProcessedAt` and `Reason`; when it does not come — CoreBank publishes nothing for a
replayed cancellation — the payment is still proven, and the card says so. The broadcast is what
resolves a cancellation this console did not request; the response is what resolves the one it did."

---

**H4 — The burst's Sent line prints `accepted` in the proven-pass green and `failed` in the proven-failure red.**

*Where:* `DESIGN.md` Components, *Burst control*: "Sent reports what the API answered
(`counter-accepted` reuses `state-healthy`, `counter-failed` reuses `state-failed`)"; frontmatter
`components.burst-control`.

*What the spine says elsewhere:* `state-healthy` "is reserved for a proven pass … never for
'probably fine' or 'still waiting'," and the Do's and Don'ts row is literally "Use green
optimistically before evidence exists." An HTTP `202` is the console's canonical example of *not*
proven — the entire two-line counter design exists because "a burst is exactly where 'acknowledged'
and 'finished' diverge." The bar directly beneath is deliberately colourless, on the stated grounds
that "a green bar would report 187 healthy payments on a figure that counts rejections as equally
proven" — the same argument applies with more force to a figure that counts *nothing* as proven.

*On-stage failure:* from the back of a 250-seat room the takeover shows two green numbers stacked in
the same column, `accepted 200` above `Settled 187`. Green is this console's word for "proved."
The burst reads as finished before the demonstration it exists for — the gap between acceptance and
completion — has been made. The red is wrong in the mirror direction: a Dev Proxy-injected `503`
lands on the response path of a call CoreBank may well have executed, so a `failed` send is an
*unknown* outcome, and it is printed in the same red, in the same column, one row above
`rejected`, which is a proven business rejection.

*Proposed fix:* in `DESIGN.md` Components, *Burst control*: "The **Sent** line carries no state
colour at all — `counter-accepted` and `counter-failed` render in `text-primary`, for exactly the
reason the bar carries none: Sent reports what the API answered, and no answer on that line is a
proven outcome for a payment. Green on `accepted` would assert a pass the burst has not yet earned,
and red on `failed` would assert a rejection when a failed send is the one figure on this screen
whose outcome is genuinely unknown. The state palette belongs to the **Settled** line, which is
where proof is reported." Change the frontmatter tokens accordingly and add a Do's and Don'ts row:
"Print the burst's Sent figures in a neutral tone | Colour an HTTP acknowledgement as a proven pass."

---

**H5 — Failed sends are unaccounted for, and the drained takeover prints "every payment proved itself" with no stated precondition.**

*Where:* `wireframes/operations-stage-focus.md` S9 (`every payment proved itself · nothing left
awaiting`); `EXPERIENCE.md` Component Patterns, *Burst control* and *Burst takeover*.

*What the spine says:* `Sent 200 / 200   accepted 200 · failed 0` and `Settled 187   rejected 0 ·
still moving 13`, with "`still moving` is the awaiting count under a name the room can hear." What
neither file says is where a payment goes when its *send* failed. It has no proven outcome, so it
cannot be Settled or rejected; nothing states it joins `still moving`; and individual burst payments
"never enter the STILL OPEN strip and never take the focus card." It falls out of the accounting
entirely.

*On-stage failure:* Flow 2 step 5 is built on driving the `failed` figure up with real injected
`503`s. Under those conditions the drained takeover can read `Sent 200/200 accepted 188 · failed 12`
over `Settled 188 · rejected 0 · still moving 0` and, per S9, the line **`every payment proved
itself · nothing left awaiting`** — held on screen until dismissed, which makes it the
longest-lived and loudest claim the console ever renders. Twelve payments with unknown outcomes,
possibly executed, are behind that sentence. This is the burst equivalent of C4 and it is worse,
because the console states the negative out loud.

*Proposed fix:* two additions. To *Burst control*: "A payment whose send failed has no proven
outcome and is counted as one: the **Settled** line carries a fourth figure, `unknown <n>`, holding
every payment whose HTTP call failed and every payment restated as unknown by a feed loss, so
`settled + rejected + still moving + unknown` always equals `accepted + failed` and the room can do
the arithmetic from the screen." To *Burst takeover*: "The drained summary's closing line is
conditional and is printed only when it is true — `every payment proved itself · nothing left
awaiting` requires `still moving 0`, `unknown 0`, and `failed 0`. Otherwise the line states what is
outstanding: `12 payments have unknown outcomes — see Evidence`. A drained burst is not the same
thing as a proven burst, and the takeover never says it is."

---

**H6 — The transient announcement has one token — proven-failure red with `✕` — and is used to announce a feed loss, which the spine insists is not a failure.**

*Where:* `DESIGN.md` Components, *Transient announcement* and frontmatter
`components.transient-announcement`; `EXPERIENCE.md` State Patterns, *Feed lost*.

*What the spine says:* the announcement "renders `symbol` (`✕`, fallback `[X]`) and its sentence in
`foreground` (`state-failed`) — a command that ran and was refused is a proven failure, which is
exactly what that token is reserved for." Single-valued: there is no other token in the component.
But `Feed lost` routes through the same component: "A transient announcement states the drop once
(`Feed lost 12:06:02 — 3 payments have unknown outcomes`)." And the palette's own rule, twice
stated, is that a dropped subscription "proves nothing whatsoever about the payment — the money may
well have moved. Red would assert a payment failure the console has no evidence for."

*On-stage failure:* the subscription drops and the console announces it in red with a failure cross,
in the biggest single-line statement it has, at the moment 250 people are looking for a verdict.
The console has just contradicted itself: the payment rows behind the line correctly turn neutral
grey while the line above them says `✕`. The operator's own read-aloud instinct will follow the red.

*Proposed fix:* in `DESIGN.md` Components, *Transient announcement*: "The row takes the token of
the thing it announces, never a fixed one. A refused or failed command renders `✕`/`state-failed` —
a command that ran and was refused is a proven failure. A notice that is not a verdict — a lost
feed, a reconnection, a palette switch — renders `○`/`state-neutral`, the same tone the rows behind
it are taking at that instant, because an announcement must never assert a failure the states it
describes do not." Add `symbol-notice: '○'` / `foreground-notice: '{colors.state-neutral}'` to the
frontmatter token group and a Do's and Don'ts row.

---

**H7 — A refusal the console produced itself may have no durable record, and the screen it clears into is a large green SETTLED card.**

*Where:* `EXPERIENCE.md` Component Patterns, *Transient announcement*; Information Architecture
(the *Override* paragraph and the Evidence/Results capability list); Accessibility Floor, *A
transient announcement is never the only carrier*; `wireframes/operations-stage-focus.md` S10.

*What the spine says:* "It never carries a fact that is not already durable: the same line is an
Evidence/Results record from the moment it appears." That claim is the entire justification for
removing the persistent evidence strip. But Evidence/Results is specified to hold "full response
detail (code/latency/body), inbox/outbox contents, balance/conservation, **action history**, and the
live broadcast event feed" — an action history of things that were dispatched. A client-side
validation refusal (`✕ The source and destination account must differ.`, S10) never became an
action, never produced a response, and is nowhere in that list. Nothing in either file says Evidence
records attempts that the console refused on its own.

*On-stage failure:* S10 is the picture. The operator, mid-sentence, presses Submit with both account
fields the same. The refusal appears at the foot for a few seconds and clears. Above it, filling the
screen, is the **previous** payment's `● SETTLED` card with its balance legs — the console's stated
resting picture. After the transient clears, that screen is byte-identical to the screen of a
successful submission, the strip is collapsed because nothing is open, and there is no durable
record anywhere of the payment that was never sent. The operator narrates a payment the system never
received. This is the failure mode the removal of the strip was supposed to be safe against, and it
turns on a claim ("already durable") the spine asserts rather than specifies.

*Proposed fix:* two sentences, one in each file. `EXPERIENCE.md` Information Architecture,
Evidence/Results row: "…action history — **attempted actions, not only dispatched ones: every
refusal the console produced itself (validation, precondition, lock) is a record here before its
announcement is drawn**…". And in *Transient announcement*: "The refusal also outlives its own
announcement in the object that owns it: the compose-bar field the refusal names carries an inline
marker until the operator changes it, so a cleared announcement can never leave the screen looking
like a successful submission. A stale focus card is never the answer to the last keypress — the
card's meta line carries its own payment's submit time (see M3)."

---

### Medium

**M1 — The Operations feed statement has no specified wording for a lost feed.**
`DESIGN.md` Components, *Feed status, inline* gives `foreground-lost` and `symbol-lost` (`○`) but no
text; `EXPERIENCE.md` gives the Evidence header's wording, the reconnect wording and the per-payment
state change, and for Operations only the positive form (`listening since 14:02:11`). Since this
pass made that single line the *only* place Operations states whether anyone is listening, the
obvious implementation — drop the line when not listening — turns the console's most important
sentence into a blank, and a blank reads as nothing wrong. *Fix:* state the negative form and that
it is never blank: "`○ not listening since 12:06:02 — outcomes will not arrive here`. The statement
is never absent and never surrendered at any width; only its words change."

**M2 — A payment submitted after a feed loss falls between two State Patterns rows.**
*Feed lost* is written as a transition applied to "every unresolved payment" at the moment of the
drop; *Feed never established* is written as a start-of-session condition ("never opened, or
unavailable on this topology"). A payment submitted while a dropped feed has not yet reconnected
matches neither, and the natural implementation gives it `Awaiting settlement` — the one label the
spine says must be structurally impossible while nothing is listening. *Fix:* add to *Feed lost*:
"The state persists until the subscription is re-established, not only for the payments outstanding
at the moment of the drop: every payment submitted while the feed is down enters at `Outcome not
observed — no feed`, exactly as under *Feed never established*, and the region statement keeps
saying so."

**M3 — The resting focus card holds a resolved payment indefinitely, with no wall-clock stamp and no provenance.**
`DESIGN.md` Components, *Focus card*: "with nothing open it holds the most recently resolved
payment" and "**most of the time the card is the only thing on this surface**." It prints a final
elapsed clock (`1.2s`) — a duration, not a time — the balance legs, and nothing about when this
happened or under what conditions. Meanwhile the Evidence-provenance rule exists precisely because
"a `202 Pending` captured under 12 seconds of injected latency and one captured under none are
different facts." *On-stage failure:* twenty minutes and one topology switch later, the biggest
object on the screen still shows a green settlement with balances from a topology that no longer
exists, unmarked, while the operator talks about something else and the room reads it as current.
*Fix:* "The card's meta line carries its payment's own submit time alongside its id, and when the
payment it holds was captured under a different topology, run generation or fault level than the one
now in force, it carries the same visible stale marking every Evidence record carries. A topology
switch returns the card to its placeholder rather than leaving a resolved payment from a topology
that is gone."

**M4 — Whether an `Outcome unknown` payment stays in the STILL OPEN strip is never stated.**
The strip "lists every payment with no proven outcome yet" and "a payment leaves the strip the
moment its outcome is proven" — by which `Outcome unknown` stays, correctly. But every other
sentence about the strip talks about *awaiting* payments, and the natural implementation lists what
is awaiting. These are the payments most in need of listing. *Fix:* one sentence in *Still-open
strip*: "`Outcome unknown` payments stay in the strip and are not a separate case: unknown is not
proven. They are the payments the operator most needs listed, and each carries **Look up outcome**
on its own line."

**M5 — The focus card's state list omits `Ambiguous`.**
`DESIGN.md` `focus-card` enumerates five foregrounds — settled, rejected, awaiting, cancelled,
unknown — and `EXPERIENCE.md`'s card row names the same set. `Payment: Ambiguous` (an Omitted-key
outcome that could not be reconciled, Resend disabled) has no card entry. Since `state-ambiguous`
and `state-running` are the same hex, an implementer mapping it to awaiting produces a card that
reads as legitimately waiting for a payment whose whole point is that it is not. *Fix:* add
`foreground-ambiguous` to the card's token list and a card sentence: "an ambiguous payment renders
`~ AMBIGUOUS` with its closing block reading `not yet reconciled — Resend is unsafe`, and its action
slot holds a disabled **Resend same key** beside an always-enabled **Look up outcome**."

**M6 — The takeover's caption and denominator after Stop sending.**
The captioned rule is specified as "the elapsed time at the right end while it runs and the **drain
time** once it is done," and S9 renders `drained in 9.8s`. A burst the operator stopped at 120 of
200 did not drain; the 80 payments that were never sent are indistinguishable on the bar (`120 of
200 proven`, permanently partial) from 80 that were sent and unproven. *Fix:* "A stopped burst is
captioned distinctly — `stopped after 6.2s · 120 of 200 sent` — and the bar's denominator becomes
the sent count, with the unsent remainder stated as its own figure, so a partial bar is never read
as payments that failed to prove themselves."

---

### Low

**L1 — The bar caption's numerator is unstated and collides with the Settled figure.**
`DESIGN.md` says the bar "measures how much of the burst has proved itself *either way*"; S8 renders
`187 of 200 proven` directly beneath `Settled 187`. The two numerals coincide only because
`rejected` is 0 in every drawn screen. With rejections they diverge, and nothing says which number
the caption carries. *Fix:* caption it `188 of 200 proven (settled or rejected)`.

**L2 — `Sent 200 / 200` does not say what the denominator is.**
It is the requested count, which is right, but the line is read aloud and "two hundred of two
hundred sent" is one word away from "two hundred of two hundred done." *Fix:* the line label already
does the work; state in *Burst control* that the denominator is always the requested count and never
changes, so a stopped or partially-sent burst cannot silently re-base it.

---

## Findings I am not sure about

1. **Whether Evidence/Results genuinely holds everything the removed strip announced.** I could
   check only what the spines claim about it. The Evidence capability list has not changed in this
   pass, and the transient's durability claim (H7) is asserted in three places without a
   corresponding sentence anywhere in Evidence's own specification. If Evidence already records
   refused attempts in the built console, H7 is a documentation gap rather than a defect; if it does
   not, H7 is the highest-value fix in this file. Someone should check `CoreBankDemo.DemoRunner`'s
   action-history writer before deciding which.

2. **`Resend same key` on a cancelled payment.** S3 shows `[ Look up outcome ]` in the action slot
   after a cancellation, but the spine's rule is that Resend is offered "once a Generated/Supplied
   submission exists that can be resent." PaymentsAPI replays `504 Cancelled` with the *persisted*
   cancellation timestamp for a cancelled key, so a Resend would produce no payment and a card that
   reads `CANCELLED` again with a stale `ProcessedAt`. That is arguably truthful — the two-clocks
   rule would surface the staleness — but it is not stated anywhere, and I could not tell whether
   the card is meant to offer Resend in that state at all.

3. **`⊘` at projector distance, and `state-neutral` for a proven outcome.** `DESIGN.md` flags the
   glyph as an untested `[ASSUMPTION]` with an `[/]` fallback, which is the right treatment. My
   residual doubt is the tone: `CANCELLED` is a proven outcome rendered in the palette's dimmest
   state colour, one step below the card's own detail text and identical to `Outcome unknown`. The
   elimination argument for it is sound and the upper-case word carries the meaning, so I am not
   raising it as a finding — but it is the one state I would want to see on an actual projector
   before the talk.

4. **Whether the console's cancel, issued directly to CoreBank at `127.0.0.1:5032`, leaves
   PaymentsAPI's view of the payment out of step with the card.** `PaymentsController` does replay
   `504 Cancelled` when either the snapshot or the cached status is `Cancelled`, which appears to
   close the gap, but the timing of when PaymentsAPI learns is not something I traced. It matters
   only for a re-submission of the same key during the same talk.

---

## Prior pass, re-checked

All ten findings from the 2026-09-03 pass remain resolved in the current text and none is re-raised
here. Spot-checked by search rather than assumed: recovery-on-relaunch is intact and still bans any
journal/checkpoint claim (Foundation); burst Cancel is still the named lock exemption and still
wears the teal signature; Reset still has exactly one stated location (Run's first internal phase,
four places); every resource/AppHost transition still carries its elapsed readout; the fault chip is
still persistent across all five workspaces — it survived the bottom-band removal because it lives
in the topology bar, which this pass did not touch; Load Test's Run is still modal-gated; evidence
provenance across a switch is intact and now also carries fault levels; and the `Ambiguous` line is
still the corrected single-sentence form.

## What is already strong

- **The cancel state model is right where it is hardest.** A cancellation is ranked with the proven
  outcomes, carries its own symbol and word, is never dressed as a failure or as a success, and
  `DESIGN.md` reaches `state-neutral` by elimination with the reasoning written down. The rule that
  a cancel arriving after commitment renders *the outcome that actually happened*, legs included, is
  exactly the right instinct, and "the bank's answer wins" is the sentence the whole feature needed.
- **`Outcome unknown` is never entered by a timeout, only by the feed's own status changing**, and
  the cancel path is explicitly given no shortcut around that rule ("asking for a cancellation is
  not evidence of one"). That sentence is the single best line in this pass.
- **The removal of the persistent evidence strip is better defended than it first looks.** The
  argument that the strip was never the record, that outcomes belong to the object they are the
  outcome of, and that the transient announces failures and refusals *only* — never settlements — is
  correct and correctly scoped. C1 and H7 are holes in that argument, not refutations of it.
- **`still moving` is a genuinely better name than "proven leg"**, it is explicitly never a countdown
  and never decremented by a timeout, and the proportional bar is deliberately colourless with the
  reasoning stated. The bar's denominator is the requested count, so it can under-fill but never
  overstate.
- **The strip's zero state is argued rather than assumed**, and the one thing that must never vanish
  with it — the feed statement — is explicitly relocated rather than dropped. That relocation is
  specified for every Operations state except the burst takeover, which is why C1 is a hole in an
  otherwise complete rule.
- **Colour independence holds across all five new components**: the card carries state in symbol,
  upper-case word, fixed position and whitespace with an explicit four-channel argument; the strip
  carries selection on `▸` rather than a fill; the takeover carries its identity in a captioned rule.
  The two failures of this rule in the current text (H4, H6) are both in `DESIGN.md` token blocks
  rather than in the reasoning, which is the cheaper place to fix them.
