# Remediation brief — resolving the stage-safety and structure lenses

Both lenses returned Fail. Every finding below is accepted unless marked REJECTED.
Read `review-stage-safety.md` and `review-structure.md` for the full argument on each;
this file carries the RESOLUTION to apply, which sometimes differs from the fix the
reviewer proposed. Where they differ, this file wins.

## 0. A correction that changes several findings

I asserted that CoreBank identifies a payment by a server-generated `TransactionId` the
console cannot know until the response arrives. That was wrong, and the code is explicit:
`PaymentStorageHandler` computes `var key = idempotencyKey ?? Guid.NewGuid()` and then
sets both `IdempotencyKey = key` and `TransactionId = key`. In Generated and Supplied
idempotency modes the console therefore holds the transaction id **before** it sends the
request. Consequences:

- Cancel is live for the whole of an instant payment's budget window, which is the
  capability the operator asked for.
- In **Omitted** mode alone the console has no id until the bank answers, so Cancel
  renders disabled with that reason stated. This is a further consequence of the mode
  already labelled "not retry-safe after an ambiguous outcome" and should be written as
  one, not as a separate limitation.

## 1. Criticals

**C1 — Burst takeover carries no feed statement.** The takeover replaces every surface
that says whether the console is listening. Resolution: the takeover's captioned rule
carries the feed statement at its right end, exactly as the STILL OPEN strip's rule does.
If the feed drops mid-burst, the proven counter stops being able to claim anything: the
`Settled` line must re-state itself as `outcome unknown` for the payments it can no
longer account for, in `state-neutral`, never freezing at a stale number that reads as
truth.

**C2 — `504 Cancelled` is in the shipped system and in neither spine.** Since commit
`73ccbb1` the instant rail answers `504` with `Status: Cancelled` when its budget is
exhausted and the withdrawal is proven; the console already classifies it
(`HttpPaymentGateway.cs:163`, `OperatorModels.cs:88`). A residual `202` now remains only
when the cancel outcome is genuinely unknown. Resolution: add `504 Cancelled` to
Foundation's HTTP vocabulary beside `202 Pending`; correct every Key Flow beat that
narrates `202 Pending` for a budget-exhausted instant payment; state that a `504` whose
body does not say `Cancelled` is still an ordinary gateway failure.

**C3 — The `409` wording asserts live execution.** `TransactionCancellationOutcome.InFlight`
is documented in code as *Processing under a live claim, terminally `Failed`, or lost the
claim race*. Resolution: never assert execution. The card renders the status the response
body actually carries — "the bank refused the cancellation and reports <Status>" — and
where that status is terminal the card resolves to it rather than staying awaiting.

**C4 — A submission whose response never arrives has no state anywhere.** Resolution: the
focus card appears the moment Submit is pressed, carrying the request, a running clock and
a live Cancel (disabled in Omitted mode, per §0). A response that never arrives leaves the
card reading that no answer has come, with the clock running. It never reads Failed, and
the still-open strip counts it.

## 2. Highs

**H1** — the focus card's "exactly one closing block, never two" makes a Contradiction
unrenderable. Resolution: Contradiction is the single named exception and renders both
claims with their sources and times, consistent with the standing rule that the console
has no tie-break and must never acquire one.

**H2** — cancel has no in-flight state and no answer for a reply that never arrives.
Resolution: give the action a dispatched state; a cancel whose reply never arrives leaves
the payment exactly as it was and says the cancellation is unconfirmed. It never implies
the payment was withdrawn.

**H3** — what proves `CANCELLED` is stated two ways. Resolution: align to the shipped
model. The `200 Cancelled` is CoreBank's own committed statement and proves the
withdrawal; the broadcast `com.corebank.transaction.cancelled` confirms it. The card does
not hang waiting for the event, and a contradicting event produces a Contradiction.

**H4** — the Sent line colours `accepted` green and `failed` red. Resolution: the Sent
line is transport, not proof, and carries no state colour at all. Only the Settled line
may use the proven-pass and proven-failure tokens.

**H5** — failed sends unaccounted for, and "every payment proved itself" is unconditional.
Resolution: the drained takeover states that sentence only when settled plus rejected
equals sent and nothing is awaiting; otherwise it states the shortfall in the same place.

**H6** — the transient announcement has one token, proven-failure red. Resolution: it
takes two forms, a refusal (red) and a neutral notice (`state-neutral`), because a lost
feed is not a failure. Reuse the existing tokens; add no new colour.

**H7 — CONFIRMED IN CODE, load-bearing.** The console's refusal paths return through
`RejectedPayment` without writing an Evidence entry, so those messages exist only on the
line this pass deletes. Resolution: state as a spine requirement that every refusal or
failure the console produces itself is recorded in the Evidence workspace **before** the
transient announces it. The transient is never the only record. Removing the bottom band
is conditional on this.

## 3. Mediums and lows

Apply M1-M6 and L1-L2 from `review-stage-safety.md` as written; they are wording
specifications, not design changes. Two clarifications: an `Outcome unknown` payment
**stays** in the still-open strip, because nothing proved it finished (M4); and the
resting focus card carries a wall-clock stamp so a resolved payment left on screen cannot
be mistaken for a fresh one (M3).

## 4. Structure findings

Apply all of `review-structure.md`. Three need a stated resolution:

- **S-01** — both files claim the wireframes were drawn at 74 columns and understate the
  width. That stopped being true when they were redrawn at 80 and 71. Delete the claim;
  the wireframes are current.
- **S-02** — the strip's visibility rule is written two ways. The correct rule, and the
  one the operator approved from the rendered preview, is: **the strip renders only when
  more than one payment is open.**
- **S-07 / S-08** — the no-history argument runs in four places and the band-removal
  argument in five, two of them in DESIGN.md, which owns neither. Argue each once in
  EXPERIENCE.md and cross-reference everywhere else.

## 5. Standing constraints

Ownership boundaries hold. No new colour token in either palette. Section order locked.
Where a documented decision is overturned, say so rather than deleting it silently.
