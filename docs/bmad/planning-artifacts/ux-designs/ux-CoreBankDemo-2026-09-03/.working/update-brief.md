# Update brief — Operations workspace redesign, cancel affordance, currency removal

Every decision below is confirmed by the user (Loek). Source of record is `.memlog.md`
entries 80-93. Wireframes: `.working/operations-stage-focus.md`.

## 1. Operations workspace becomes Stage-focus

Three regions, top to bottom, on one continuous `surface-base` tone separated by
captioned hairline rules:

1. **Compose bar** — two lines. First: `From <iban>  →  To <iban>` with captions, and
   `[ Submit ]` anchored at the right edge. Second: `Amount`, `Rail` chip, `Key` chip,
   and `[ Burst… ]` anchored at the right edge. A third line appears only in Supplied
   mode (the supplied-key field) or Omitted mode (the not-retry-safe warning).
2. **Focus card** — one large object rendering the selected payment: state symbol and
   an upper-case state word, elapsed or final clock, the payment's own action anchored
   right, then the request detail, the transaction id, and either the lifecycle in words
   ("submitted ──▶ 202 Pending ──▶ waiting for the bank"), the balance legs, or the
   `ErrorReason`.
3. **STILL OPEN strip** — REVISED, supersedes the EARLIER history list. It lists ONLY
   payments with no proven outcome yet, one compact line each, under a captioned rule
   whose right end carries the feed status. Account identifiers truncate to their last
   four digits here; the focus card always prints them in full. The strip collapses to
   zero rows, rule included, when nothing is open, so a settled card owns the whole
   screen. The feed status then moves to the foot of the card region, right-aligned.
   Session history is NOT shown in Operations at all: it lives in the Evidence
   workspace, which already holds the action record and the live event feed one keypress
   away. Duplicating it here was the space cost the operator objected to.

Selection drives the focus card. With nothing open the card holds the most recently
resolved payment, so a settled result stays readable. With exactly one payment open it
holds that one. With more than one open, selection moves among the strip and the card
follows, which is what lets Cancel target a specific payment during the outage climax
where a standard and an instant payment are both in flight.

## 2. Burst takes over the whole content area

While a burst runs it replaces compose bar, focus card and EARLIER list entirely. On
drain it holds a final summary until dismissed, so the room can read the result.
Individual burst payments still never enter the payment list; they are counted.

## 3. Burst counter lines renamed

`HTTP leg` becomes **Sent**, `Proven leg` becomes **Settled**. Wording:
`Sent  200 / 200   accepted 200 · failed 0` and
`Settled  187   rejected 0 · still moving 13`. The old names failed the read-aloud test
on their own author. A proportional bar reinforces the printed numbers and never
replaces them.

## 4. The bottom band is removed from every workspace

The footer shortcut strip, the in-flight action line and the evidence line all go, in all
five workspaces, so the shell stays uniform. A failed or refused command announces itself
as a transient line at the foot of the content area and then clears. The transient is an
announcement only: the durable record of every action stays in the Evidence workspace,
and no state is ever carried by the transient alone. This preserves the existing spine
invariant that silence is never an outcome.

## 5. Cancel a running payment

New capability. Offered on any payment with no proven outcome yet, standard rail
included, because CoreBank's `POST /api/transactions/cancel` is rail-agnostic. It acts on
the payment in the focus card, fires immediately with no confirmation modal, and is not
gated by the single-action-in-flight lock's `Y` confirmation. Justification for skipping
the modal: a cancelled payment is safe to retry with a new key, so the action is not
destructive in the sense that stopping a resource is.

Three outcomes the console must render distinctly and truthfully:

| Bank's answer | Card shows |
|---|---|
| Withdrawn before execution (`200 Cancelled`) | `⊘ CANCELLED`, "withdrawn before execution · no money moved", "safe to retry with a new key" |
| Already executed (`200` with the committed response) | `● SETTLED` (or `✕ REJECTED`), "too late to cancel — the bank had already executed it", plus the balance legs |
| Mid-execution, refused (`409`) | Card stays awaiting, "the bank is executing it right now" |

The resolution reaches the console through the existing outcome feed: CoreBank publishes
`com.corebank.transaction.cancelled` on `transaction-events`, so a cancelled payment
resolves the same way a settled one does. That event type is now a fourth member of the
outcome contract alongside completed, failed and balance-updated. Cancel adds no new
endpoint to any banking service; the console calls the one CoreBank already exposes.

## 6. Currency stops being an operator input

Removed from the form and from the console's validation rules. It stays required on the
wire, so the console sends a fixed `EUR`. Assumption pending confirmation that every
seeded demo account is EUR.

## 7. "Outcome key" is retired as a label

The manual outcome lookup survives as an always-enabled action on the focus card
(`[ Look up outcome ]`) rather than a standing captioned text field. It remains the
documented remedy whenever the feed is lost.

## 8. Primary-action placement rule is overridden for Operations only

Submit sits in the compose bar; the payment's own action sits on the focus card. Actions
anchor to the object they operate on. Resources, Faults and Load Test keep the fixed
lower action region.

## 9. Navigation rail shrinks

The rail goes from 22 columns to 16 in its preferred state; the compact 5-column state is
unchanged. Its five labels, longest `Load Test`, need 14 columns plus 2 of border. The six
columns return to every workspace. At a 100-column terminal the workspace inner width
becomes 80, and at the 80-column floor with the compact rail it is 71.

## Row budget at 100x30

| Rows | Today | Proposed |
|---|---|---|
| Shell chrome | 10 | 7 |
| Controls | 16 | 3 |
| Payment area | 4 | 20 |
