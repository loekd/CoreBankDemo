# Validation Report — CoreBankDemo DemoRunner (Operations Stage-focus pass)

- **DESIGN.md:** `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/DESIGN.md`
- **EXPERIENCE.md:** `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/EXPERIENCE.md`
- **Wireframes:** `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/wireframes/operations-stage-focus.md`
- **Run at:** 2026-09-09

## Overall verdict

**This report describes work that is finished.** Both gate lenses returned **Fail**. All
41 confirmed findings were accepted — none was rejected — and every one has since been
applied to the spines. The verdicts below are the state the lenses found, not the state
the spines are in now; each finding carries the resolution that was applied, and where the
resolution differs from the fix the reviewer proposed, the resolution is what landed.

Two lenses ran as the reviewer gate: **live-stage safety and recoverability** and
**structure**. Stage safety found 4 Critical, 7 High, 6 Medium and 2 Low, and refused the
build outright — "do not build the cancel affordance or the burst takeover from these
spines as written." Structure found 1 Critical, 7 High, 7 Medium and 7 Low, most of them
ownership and restatement defects introduced by the redesign itself. The two most serious
findings were both about the console asserting something it had not proven: a burst
takeover that deletes every surface carrying the feed statement, and a `504 Cancelled`
answer that exists in the shipped system and in neither spine while three Key Flows
narrate `202 Pending` in its place.

**Coverage is partial, and deliberately so.** The rubric walker was offered and declined
for this pass, so **none of the eight rubric categories was assessed**. The rubric review
on disk (`review-rubric.md`) belongs to the superseded 2026-09-03 pass and predates the
Stage-focus redesign, the six new components and the twelve new spacing tokens; its
verdicts are stale and are **not** carried forward here. No accessibility lens ran this
pass either. What this report covers is what the two lenses that ran actually looked at.

The **prose lens** ran afterwards, at polish, as the configured document standard rather
than as a gate lens. It is reported separately below because it is not a gate verdict —
but it was not cosmetic: it made ten substantive corrections beyond copy-editing and
surfaced four wireframe-versus-spine conflicts, all four since resolved.

## Category verdicts

Rubric categories — **not assessed in this pass** (rubric walker declined):

- Flow coverage — not assessed
- Token completeness — not assessed
- Component coverage — not assessed
- State coverage — not assessed
- Visual reference coverage — not assessed
- Bloat & overspecification — not assessed
- Inheritance discipline — not assessed
- Shape fit — not assessed

Lenses that did run:

- Live-stage safety and recoverability — **Fail** (all findings resolved)
- Structure (ownership, restatement, contradiction, token integrity, numbers) — **Fail** (all findings resolved)
- Prose (document standard, post-gate) — applied; ten substantive corrections, four wireframe conflicts resolved

The structure lens did report clean results on ground adjacent to two rubric categories —
zero dangling token references, both palettes holding identical 16-key sets, every
component paired across the two files, and the row/column arithmetic adding up — but that
is the structure lens's own integrity check, not the rubric's token-completeness or
component-coverage assessment, and it is not a substitute for one.

## Findings by severity

Counts are taken from the two review files: **5 Critical, 14 High, 13 Medium, 9 Low — 41
confirmed.** Stage safety additionally listed 4 items it could not settle; structure
listed 5 suspicions. Both sets are recorded under *Unsettled items* below.

### Critical (5)

**[Stage safety] C1 — The burst takeover carries no feed statement, so the console's most-watched screen cannot say whether anyone is listening.**
(§ `EXPERIENCE.md` Component Patterns, *Burst takeover* / *Feed status (inline)*; `DESIGN.md` Components; wireframes S8, S9)
The takeover "**replaces** the compose bar, the focus card and the STILL OPEN strip" —
precisely the two places the feed statement is permitted to live. For the whole of a
200-payment burst the screen shows `Settled 187 · still moving 13` with nothing anywhere
saying the console is still hearing anything. If the subscription drops, the counter
freezes and silently changes meaning from "13 payments the console is waiting to hear
about" to "13 payments nobody is listening for." The operator holds the room on a number
that has quietly stopped being true.
**Resolution (applied):** the takeover's captioned rule carries the feed statement at its
right end, exactly as the STILL OPEN strip's rule does. On feed loss the **Settled** line
restates the payments it can no longer account for as `outcome unknown` in
`state-neutral`, rather than freezing at a stale number that reads as truth. Both burst
wireframes were redrawn carrying the statement on their rule.

**[Stage safety] C2 — `504 Cancelled` exists in the shipped system and in neither spine; three Key Flows narrate `202 Pending` for a payment the rail has already withdrawn.**
(§ `EXPERIENCE.md` Voice and Tone, State Patterns, Flow 1 step 3, Flow 2 step 6, Flow 4 failure branch)
Verified against `PaymentsAPI/Controllers/PaymentsController.cs`: on the instant rail,
`InstantDeliveryOutcome.Cancelled` answers **`504` with a frozen body whose `Status` is
`Cancelled`**; only `Deferred` answers `202`. Searching both spines for `504` returned
nothing. Flow 2 step 6 is the worst case — it is the beat where the operator narrates
cancelling a hanging payment that may already have answered `504 Cancelled` at
submission: "He would be claiming to cause an outcome the system produced on its own."
**Resolution (applied):** `504 Cancelled` added to Foundation's HTTP vocabulary beside
`202 Pending` (the memlog records it now present in eight places in `EXPERIENCE.md`);
every Key Flow beat narrating `202 Pending` for a budget-exhausted instant payment
corrected; a `504` whose body does not say `Cancelled` is stated to remain an ordinary
gateway failure. This also drove a new wireframe — a rail-initiated withdrawal at nine
seconds — and, later, a seventh card state word, `CANCELLED BY THE RAIL`.

**[Stage safety] C3 — The `409` branch prints "the bank is executing it right now" for an answer that also means terminally failed.**
(§ `EXPERIENCE.md` State Patterns, *Payment: Cancel refused — mid-execution (`409`)*)
`TransactionCancellationOutcome.InFlight` — the only outcome mapped to `Conflict()` — is
documented in `TransactionCancellationHandler.cs` as *Processing under a live claim,
terminally `Failed`, or lost the claim race*. The console had the answer in hand and
printed a sentence that disagreed with it, then kept the clock climbing on a payment that
had already terminally failed — unretractable, because nothing will ever arrive to
correct it.
**Resolution (applied):** never assert execution. The card renders the status the response
body actually carries — "the bank refused the cancellation and reports <Status>" —
and where that status is terminal the card resolves to it rather than staying awaiting.

**[Stage safety] C4 — A submission whose response never arrives has no payment state anywhere, and the strip's collapse then reads as "nothing is happening."**
(§ `EXPERIENCE.md` Foundation, Component Patterns *Still-open strip*, State Patterns; `DESIGN.md` Components, *Still-open strip*)
Under 40% injected errors the response never comes back, so there is no id to correlate,
nothing to put in the strip, nothing to select, and nothing for **Look up outcome** to
look up. The screen returns to a calm, settled-looking picture while a payment that may
have moved money is outstanding and untracked.
**Resolution (applied):** stronger than the fix proposed. A correction recorded in the
remediation brief §0 established that `PaymentStorageHandler` sets `IdempotencyKey` and
`TransactionId` to the same value, so in Generated and Supplied modes the console holds
the id **before** it sends. The focus card therefore appears at the Submit keypress
carrying the request, a running clock and a live **Cancel**; a response that never arrives
leaves the card reading that no answer has come, with the clock running. It never reads
Failed, and the still-open strip counts it. In Omitted mode alone Cancel renders disabled
with its reason stated, written as a further consequence of the mode already labelled
"not retry-safe after an ambiguous outcome."

**[Structure] S-01 — Both spines misdescribe the wireframes they are derived from; DESIGN's column-token derivation rests on the false premise.**
(§ `DESIGN.md:431`; `EXPERIENCE.md:89`, `:123`, `:237`)
The spines claimed the drawings "under-draw the real line by six cells" and "understate
the available width by six columns throughout." Measured: every frame S1–S10 is 82
characters wide → **80 usable inner columns**, and S11 is 73 → **71 inner**. The drawings
are at the post-update widths, exactly. `EXPERIENCE.md:89` contradicted itself inside one
sentence, and the false provenance was repeated in four places, so a reader who checked
the reference found the spine wrong about it.
**Resolution (applied):** the six-column under-draw claim deleted everywhere; the
wireframes are current and the reserved columns are stated as measured against the real
80/71 lines.

### High (14)

**[Stage safety] H1 — The focus card's "exactly one closing block, never two" makes a contradicted outcome unrenderable on the one object the room reads.**
(§ `EXPERIENCE.md` Component Patterns *Focus card*, State Patterns *Contradicted outcome*)
An instant-rail `200 Completed` followed by `com.corebank.transaction.failed` — which the
spine itself calls "the most valuable thing the feedback loop can surface" — leaves the
largest object on the screen able to render only one of the two facts, and the
disagreement discoverable only by switching workspaces.
**Resolution (applied):** Contradiction is the single named exception to the one-block
rule and renders both claims with their sources and their times, consistent with the
standing rule that the console has no tie-break and must never acquire one.

**[Stage safety] H2 — Cancel has no in-flight state and no answer for a reply that never arrives.**
(§ `EXPERIENCE.md` Component Patterns, *Cancel payment action*)
Cancel is the one action that skips the modal, so there is not even a modal as evidence it
was pressed. Every other command in the console carries a "still working, not hung"
readout. Under an 800–2000 ms latency band the operator presses it, nothing changes, and
he presses it again — and a replayed cancellation publishes nothing.
**Resolution (applied):** the action gets a dispatched state. A cancel whose reply never
arrives leaves the payment exactly as it was and says the cancellation is unconfirmed. It
never implies the payment was withdrawn.

**[Stage safety] H3 — Whether `⊘ CANCELLED` is proven by the `200` or by the broadcast is stated both ways, and one reading hangs the card forever.**
(§ `EXPERIENCE.md` Component Patterns *Cancel payment action*, State Patterns, Flow 2 step 6)
Three statements, two answers. Under the waiting reading, a replayed cancellation — which
CoreBank documents as publishing no event — leaves the card at `~ AWAITING SETTLEMENT`
permanently while the console holds a `200 Cancelled` in hand.
**Resolution (applied):** aligned to the shipped model. The `200 Cancelled` is CoreBank's
own committed statement and proves the withdrawal; the broadcast confirms it. The card
does not hang waiting for the event, and a contradicting event produces a Contradiction.

**[Stage safety] H4 — The burst's Sent line prints `accepted` in the proven-pass green and `failed` in the proven-failure red.**
(§ `DESIGN.md` Components, *Burst control*)
`state-healthy` is "reserved for a proven pass … never for 'probably fine' or 'still
waiting'," and an HTTP `202` is the console's canonical example of not proven. From the
back of the room the takeover showed two green numbers stacked in one column, `accepted
200` above `Settled 187` — the burst reading as finished before the demonstration it
exists for has been made.
**Resolution (applied):** the Sent line is transport, not proof, and carries no state
colour at all. Only the **Settled** line may use the proven-pass and proven-failure tokens.

**[Stage safety] H5 — Failed sends are unaccounted for, and the drained takeover prints "every payment proved itself" with no stated precondition.**
(§ wireframe S9; `EXPERIENCE.md` Component Patterns, *Burst control* / *Burst takeover*)
A payment whose *send* failed has no proven outcome, cannot be Settled or rejected, and is
not stated to join `still moving` — it falls out of the accounting entirely, while the
takeover holds **`every payment proved itself · nothing left awaiting`** on screen until
dismissed. Twelve payments with unknown outcomes can sit behind the longest-lived and
loudest claim the console ever renders.
**Resolution (applied):** the drained takeover states that sentence only when settled plus
rejected equals sent and nothing is awaiting; otherwise it states the shortfall in the
same place.

**[Stage safety] H6 — The transient announcement has one token — proven-failure red with `✕` — and is used to announce a feed loss, which the spine insists is not a failure.**
(§ `DESIGN.md` Components, *Transient announcement*; `EXPERIENCE.md` State Patterns, *Feed lost*)
The console contradicts itself on screen: the payment rows behind the line correctly turn
neutral grey while the line above them says `✕`. "The operator's own read-aloud instinct
will follow the red."
**Resolution (applied):** the announcement takes two forms — a refusal (red) and a neutral
notice (`state-neutral`), because a lost feed is not a failure. Existing tokens reused; no
new colour added to either palette.

**[Stage safety] H7 — A refusal the console produced itself may have no durable record, and the screen it clears into is a large green SETTLED card.**
(§ `EXPERIENCE.md` Component Patterns *Transient announcement*, Information Architecture; wireframe S10)
The claim "it never carries a fact that is not already durable" is the entire
justification for removing the persistent evidence strip — and it was asserted, not
specified. **Confirmed in code:** the console's refusal paths return through
`RejectedPayment` without writing an Evidence entry, so those messages exist only on the
line this pass deletes. After the transient clears, the screen is byte-identical to a
successful submission and the operator narrates a payment the system never received.
**Resolution (applied):** stated as a spine requirement that every refusal or failure the
console produces itself is recorded in the Evidence workspace **before** the transient
announces it. Removing the bottom band is explicitly conditional on this.

**[Structure] S-02 — The still-open strip's visibility rule contradicts itself inside DESIGN.md, and DESIGN contradicts EXPERIENCE.**
(§ `DESIGN.md:429`, `:437` vs `:462`, `:463`; `EXPERIENCE.md:79`, `:126`, `:241`)
`:429` and `:437` both yield one row when one payment is open; `:462` says the opposite,
and `:463`'s "zero state" is written as if zero-open were the only collapsed case.
**Resolution (applied):** the decided rule — the one the operator approved from the
rendered preview — is that **the strip renders only when more than one payment is open**.
`:429`/`:437` rewritten to "while more than one is open, and nothing at all otherwise";
`:463`'s "zero state" becomes "collapsed state (nothing, or one payment, open)". The
update brief's conflicting "when nothing is open" was corrected to match.

**[Structure] S-03 — Stale "three CloudEvents" claim survives the pass that made the contract four.**
(§ `EXPERIENCE.md:263`)
Foundation now states four types are "the entire contract", and
`TransactionCancelledEvent.cs` exists in the repo with exactly the fields claimed.
**Resolution (applied):** "The four CloudEvents already exist and PaymentsAPI already
consumes them." The memlog records the three-CloudEvent claim as verified gone.

**[Structure] S-04 — A workspace-wide rule about the fixed lower action region that no longer holds everywhere is still asserted unqualified.**
(§ `DESIGN.md:475`, `:492`)
Operations has no such region, and the Do's and Don'ts table both mandated the rule at
`:492` and recorded its Operations override at `:518`.
**Resolution (applied):** `:475` scoped to the workspaces that keep the region; `:492`
carries the Operations exemption and points at the object-anchoring row.

**[Structure] S-05 — Both files still describe every workspace's content as a list of Activity rows, which Operations no longer is.**
(§ `EXPERIENCE.md:83`; `DESIGN.md:457`)
Faults was named "the one workspace" that is not a row list, while Operations' content is
now a compose bar, a focus card and one-line strip rows explicitly "not an Activity row's
two."
**Resolution (applied):** the claim scoped to the three row-list workspaces, with
Operations named as the three-region Stage-focus layout obeying the same borderless,
one-tone rules.

**[Structure] S-06 — Reference to a surface this update deleted.**
(§ `DESIGN.md:459`, Event row)
"condensed, **beneath the originating payment row in Operations**" — Operations has no
payment rows any more.
**Resolution (applied):** "…and, condensed, as the closing block of the Operations focus
card."

**[Structure] S-07 — The "no history in Operations / the strip collapses so the card owns the screen" argument is made four times, twice in the wrong file.**
(§ `EXPERIENCE.md:79`, `:126`; `DESIGN.md:462`, `:463`)
`DESIGN.md:463` re-argued a behavioural rationale in the visual file with no
cross-reference.
**Resolution (applied):** argued once, at `EXPERIENCE.md:79`. `:126` states the rule and
points there; `DESIGN.md:463` keeps only the visual facts and cross-references the
rationale; `:462` keeps the visual consequence and drops the re-argument.

**[Structure] S-08 — The bottom-band-removal argument is made in five places across the two files.**
(§ `EXPERIENCE.md:61`, `:63`, `:146`, `:225`; `DESIGN.md:470`, `:482`)
The identical three-part argument — the strip was never the record, the durable record is
in Evidence, "silence is never an outcome" is untouched — appears five times, twice of
them in `DESIGN.md`, which owns neither.
**Resolution (applied):** one home at `EXPERIENCE.md:63`; `:61` states placement only;
`:146` and `:225` cross-reference; `DESIGN.md:482` keeps the token and row consequences
and `:470` keeps the visual spec, both cross-referencing the rest.

### Medium (13)

**[Stage safety] M1 — The Operations feed statement has no specified wording for a lost feed.**
(§ `DESIGN.md` Components, *Feed status, inline*)
Tokens exist for the lost state but no text does, and this pass made that single line the
only place Operations says whether anyone is listening — so the obvious implementation
turns the console's most important sentence into a blank, and a blank reads as nothing
wrong.
**Resolution (applied):** the negative form is stated —
`○ not listening since 12:06:02 — outcomes will not arrive here` — and the statement is
never absent and never surrendered at any width; only its words change.

**[Stage safety] M2 — A payment submitted after a feed loss falls between two State Patterns rows.**
*Feed lost* is a transition applied at the moment of the drop; *Feed never established* is
a start-of-session condition. A payment submitted in between matches neither, and the
natural implementation gives it `Awaiting settlement` — the one label that must be
structurally impossible while nothing is listening.
**Resolution (applied):** *Feed lost* persists until the subscription is re-established;
every payment submitted while the feed is down enters at `Outcome not observed — no feed`.

**[Stage safety] M3 — The resting focus card holds a resolved payment indefinitely, with no wall-clock stamp and no provenance.**
Twenty minutes and one topology switch later, the biggest object on the screen still shows
a green settlement with balances from a topology that no longer exists, and the room reads
it as current.
**Resolution (applied):** the card's meta line carries its payment's own submit time, and
a payment captured under a different topology, run generation or fault level carries the
same stale marking every Evidence record carries. A topology switch returns the card to
its placeholder.

**[Stage safety] M4 — Whether an `Outcome unknown` payment stays in the STILL OPEN strip is never stated.**
Every other sentence about the strip talks about *awaiting* payments, and these are the
payments most in need of listing.
**Resolution (applied):** an `Outcome unknown` payment **stays** in the strip, because
nothing proved it finished. Unknown is not proven.

**[Stage safety] M5 — The focus card's state list omits `Ambiguous`.**
`state-ambiguous` and `state-running` are the same hex, so an implementer mapping it to
awaiting produces a card that reads as legitimately waiting for a payment whose whole
point is that it is not.
**Resolution (applied):** `foreground-ambiguous` added to the card's token list, with
`~ AMBIGUOUS`, a closing block reading `not yet reconciled — Resend is unsafe`, and a
disabled **Resend same key** beside an always-enabled **Look up outcome**.

**[Stage safety] M6 — The takeover's caption and denominator after Stop sending.**
A burst stopped at 120 of 200 did not drain, and the 80 payments never sent are
indistinguishable on the bar from 80 sent and unproven.
**Resolution (applied):** a stopped burst is captioned distinctly
(`stopped after 6.2s · 120 of 200 sent`) and the **bar's** denominator becomes the sent
count. Reconciled with L2: the bar re-bases on a stopped burst; the Sent line never does.

**[Structure] S-09 — "Exactly two places … no third place" for the feed status is false against EXPERIENCE.**
(§ `DESIGN.md:456` vs `EXPERIENCE.md:141`, `:180`, `:245`)
Evidence/Results keeps its header *and* its inline per-row `(listening)` form — a third
place, in the same component.
**Resolution (applied):** the enumeration now names each place explicitly rather than
asserting a count, and keeps "never a chip." Note the cross-lens interaction: stage-safety
C1 adds the burst takeover's captioned rule as a further place, so the corrected
enumeration had to absorb both findings rather than either one alone.

**[Structure] S-10 — EXPERIENCE re-derives DESIGN's rail-width arithmetic instead of citing the token.**
(§ `EXPERIENCE.md:236`, `:123` vs `DESIGN.md:427`)
The derivation of a spacing token's value is DESIGN's job and was already stated there in
the same words; both the ownership line and the argue-once rule were crossed.
**Resolution (applied):** EXPERIENCE keeps the behavioural consequence and cites
`DESIGN.md` Layout & Spacing for the derivation.

**[Structure] S-11 — Behaviour specified in DESIGN: the burst takeover's lifetime and its action-slot labels.**
(§ `DESIGN.md:468` vs `EXPERIENCE.md:130`)
**Resolution (applied):** DESIGN keeps the visual invariant — the slot never holds two
meanings at once and both labels land in the same cells — and cross-references EXPERIENCE
for when each is shown.

**[Structure] S-12 — Behaviour specified in DESIGN: the currency wire contract.**
(§ `DESIGN.md:461` vs `EXPERIENCE.md:123`)
What the console sends is behaviour, and DESIGN silently dropped the pending-confirmation
assumption that went with it.
**Resolution (applied):** DESIGN keeps "There is no currency field" and the visual reason,
and cross-references the compose bar for what goes on the wire. (The assumption itself was
separately closed: `DemoAccountSeeder` inserts exactly three demo accounts, all EUR.)

**[Structure] S-13 — The cancel-has-no-confirmation justification is argued in three places.**
(§ `EXPERIENCE.md:199`, `:127`; `DESIGN.md:464`)
**Resolution (applied):** `:127` states the rule and points to Interaction Primitives
without repeating the reason; `DESIGN.md:464` argues only the token choice.

**[Structure] S-14 — The removed "detail" band is still in the name of the surviving region.**
(§ `EXPERIENCE.md:85`, `:144`, `:192`, `:253`; `DESIGN.md:475`)
Five sites still called it the "fixed lower action/**detail** region," so the shell read as
still having a detail band in three workspaces.
**Resolution (applied):** global rename to "fixed lower action region" at all five sites.

**[Structure] S-15 — A visual value specified in EXPERIENCE rather than referenced.**
(§ `EXPERIENCE.md:125`, `:224`)
Casing is a `DESIGN.md` token and an argued emphasis channel.
**Resolution (applied):** EXPERIENCE cites the token and keeps only the behavioural half —
that the state word is never abbreviated to fit.

### Low (9)

**[Stage safety] L1 — The bar caption's numerator is unstated and collides with the Settled figure.**
The two numerals coincide only because `rejected` is 0 in every drawn screen.
**Resolution (applied):** the caption names what it counts —
`188 of 200 proven (settled or rejected)`.

**[Stage safety] L2 — `Sent 200 / 200` does not say what the denominator is.**
The line is read aloud, and "two hundred of two hundred sent" is one word away from "two
hundred of two hundred done."
**Resolution (applied):** the denominator is stated to be always the requested count and
never re-based, so a stopped or partially-sent burst cannot silently change it.

**[Structure] S-16 — Dead spacing tokens.** `spacing.'0'` and `spacing.'2'`
(`DESIGN.md:67`, `:69`) are never referenced. Pre-existing, not introduced by this pass.
**Resolution (applied):** resolved per the reviewer's fix.

**[Structure] S-17 — An override that does not name what it replaces.** `DESIGN.md:463`
supersedes "the EARLIER history list" — an artifact of the update brief's revision marker,
not a component name.
**Resolution (applied):** "supersedes the session-history list the first Stage-focus drafts
placed here."

**[Structure] S-18 — Stale `sources:` frontmatter.** Neither file listed
`CoreBankDemo.ServiceDefaults/CloudEventTypes/TransactionCancelledEvent.cs`, which exists
and is load-bearing for `EXPERIENCE.md:41`.
**Resolution (applied):** added to both lists.

**[Structure] S-19 — DESIGN cites the wireframes without linking them.**
**Resolution (applied):** the same relative link `EXPERIENCE.md` uses was added.

**[Structure] S-20 — Do's and Don'ts row contradicts the three-role teal rule.**
`DESIGN.md:493` names two roles, `:502` and the Colors section name three. Pre-existing.
**Resolution (applied):** resolved per the reviewer's fix.

**[Structure] S-21 — Lock-exempt membership counted differently in the two files.**
`DESIGN.md` says "exactly three controls"; `EXPERIENCE.md:169` says every control in the
Faults workspace plus panic-off. Pre-existing, but this pass leaned on the "exactly three"
number to argue Cancel's treatment.
**Resolution (applied):** reconciled per the reviewer's fix.

**[Structure] S-22 — "There is no separate projector theme" survives the light palette.**
`EXPERIENCE.md:248` against `DESIGN.md:386-392`, whose stated purpose is presenting on a
projector. Pre-existing.
**Resolution (applied):** the light palette is named as the projector affordance and a
token swap only; what does not exist is a projector-only *layout*.

## Prose lens — post-gate document standard

The prose lens is **not a gate lens** on this project; it applies at polish through the
configured document standards, which is why structure ran once at the gate and prose once
after. It is reported here because it was not cosmetic.

It made **ten substantive corrections beyond copy-editing**, plus four over-long sentences
split and six copy-edits. The substantive ones the memlog names:

- a `504` withdrawal wrongly called a **rejection**, which collides with `Rejected` as a
  defined red state word;
- a **survival of the C3 `409` finding in `DESIGN.md`**, still asserting live execution
  after the remediation pass — the clearest evidence that the gate remediation had been
  verified in `EXPERIENCE.md` and not everywhere;
- two files specifying **two different burst bar captions**;
- **amber still listed as covering unknown**, which `state-neutral` owns by two separate
  arguments in the same file.

It also surfaced **four wireframe-versus-spine conflicts**, all introduced when the new
screens were drawn for the stage-safety findings, and **all four are resolved**. Three
were resolved toward the smaller vocabulary, with the spine winning:

- the in-flight card uses the existing awaiting tilde and the words `NO ANSWER YET` rather
  than a new ellipsis symbol with no monochrome fallback;
- the disabled Cancel keeps the same label dimmed rather than a longer one that overran the
  action slot the token derivation is built on;
- the still-open strip carries no per-line action — the remedy is one selection away on the
  card — which removed a contradiction where one file offered the action on the line and
  the other gave its rows no slot for it.

The fourth went the other way, and the **drawn screen was right**: a rail-initiated
withdrawal gets its own card state word, `CANCELLED BY THE RAIL`, distinct from the
operator-fired `CANCELLED`. The symbol and colour stay identical because the outcome for
the money is identical; only the word differs, so the room does not need the presenter to
say which of the two just happened. It is the seventh and longest card state word and it
sets the 34-cell state column. Both spines were the side that changed.

## Unsettled items

Neither lens closed these; they are recorded, not resolved.

Stage safety could not settle four:

1. Whether Evidence/Results genuinely holds everything the removed strip announced. This
   was subsequently **settled in code** and became H7's confirmation — the reviewer's own
   note that "if it does not, H7 is the highest-value fix in this file" was correct.
2. Whether `Resend same key` should be offered on a cancelled payment at all, given
   PaymentsAPI replays `504 Cancelled` with the persisted timestamp.
3. `⊘` at projector distance, and `state-neutral` for a proven outcome — raised as a
   dress-rehearsal check rather than a finding.
4. Whether the console's cancel, issued directly to CoreBank, leaves PaymentsAPI's view of
   the payment out of step with the card.

Structure listed five suspicions: wireframe **row** fidelity (neither file claims it), the
compose bar's row budget in Supplied/Omitted mode, the strip-threshold disagreement
between the update brief and the wireframes (settled by S-02's resolution), three
borderline behavioural statements in `DESIGN.md` that predate this pass, and hard-coded
state symbols in `EXPERIENCE.md` microcopy judged deliberate.

## Mechanical notes

- Severity counts here are taken from the two review files' own headline counts
  (4/7/6/2 and 1/7/7/7). `.memlog.md` records the same pass as "4 critical, 8 high, 13
  medium and 9 low across the two, 41 findings" — the total of 41 is right, but that
  per-severity split sums to 34 and understates Critical by one and High by six. The
  review files are authoritative.
- `review-stage-safety.md` supersedes an earlier stage-safety pass of the same file dated
  2026-09-03 and re-checked all ten of its findings as still resolved. `.memlog.md`'s gate
  entry describes stage safety as "declined on both prior passes," which the review file
  and the 2026-09-03 validation report both contradict.
- Both review files cite the wireframes at `.working/operations-stage-focus.md`; the file
  now lives at `wireframes/operations-stage-focus.md`. The content is the same document,
  extended to fourteen screens during remediation.
- `.memlog.md` verifies remediation completeness for `EXPERIENCE.md` explicitly. The prose
  lens later found C3 surviving in `DESIGN.md`, so `DESIGN.md`'s remediation was completed
  in two steps rather than one.
- The two lenses disagreed twice and both were reconciled: a `504`-cancelled payment
  **leaves** the still-open strip when its answer lands rather than never entering it, and
  the **Sent** line never re-bases its denominator while the burst **bar** does on a
  stopped burst.
- No new colour token was added to either palette during remediation; a cancellation
  reuses `state-neutral` by the same argument the `Outcome unknown` rule already makes.
- Section order in both spines was verified intact after remediation.

## Reviewer files

- `review-stage-safety.md` — live-stage safety and recoverability (gate lens, Fail)
- `review-structure.md` — structure (gate lens, Fail)
- `review-rubric.md` — **2026-09-03 pass only; superseded, not run here**
- `review-accessibility.md` — **2026-09-03 pass only; superseded, not run here**
- Prose lens — post-gate document standard; produced no `review-*.md` file, recorded in
  `.memlog.md`
