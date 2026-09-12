# Operations workspace — Stage-focus wireframes

> **Status:** Reference
> **Kind:** UX design
> **Original date:** 2026-09-03
> **Migrated from:** `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/wireframes/operations-stage-focus.md` on 2026-09-10
> **Related:** PR #15

Drawn at the real content-pane size. The navigation rail shrinks from 22 columns to 16,
so a 100-column terminal leaves an 82-column content frame and 80 usable columns inside
the workspace. The last screen is the 80x24 hard minimum, where the rail collapses to 5
and 71 columns remain — roomy enough that no account identifier needs truncating there.

Accounts are the seeded 18-character IBANs, shown in full on the compose bar and in the
focus card. The STILL OPEN strip is the one place they truncate to their last four
digits, because two full IBANs plus an amount, a rail and a clock overrun even 80
columns. The full identity is one keypress away in the card above.

The strip appears only when more than one payment is open. The focus card takes the
payment at the Submit keypress, before any answer, which is what makes Cancel reachable
for the whole of an instant payment's budget. With nothing or one thing
open the card owns the screen and the feed status sits at the foot of the card region.
Session history is not shown here at all: it lives in the Evidence workspace.

Removing the three-row bottom band and collapsing the sixteen-row form changes the
budget as follows.

| Rows at 100x30 | Today | Proposed |
|---|---|---|
| Shell chrome | 10 | 7 |
### S1 · Resting — nothing submitted yet

```
┌─ OPERATIONS ───────────────────────────────────────────────────────────────────┐
│  From NL91ABNA0417164300   →   To NL20INGB0001234567                [ Submit ] │
│  Amount     1.00   Rail ‹ standard ›   Key ‹ Generated ›            [ Burst… ] │
│ ────────────────────────────────────────────────────────────────────────────── │
│                                                                                │
│      No payment yet this session.                                              │
│                                                                                │
│      Fill the lines above and press Enter.                                     │
│                                                                                │
│                                                                                │
│                                                                                │
│                                                                                │
│                                                                                │
│                                                                                │
│                                                       listening since 14:02:11 │
└────────────────────────────────────────────────────────────────────────────────┘
```

### S1b · No answer yet — Cancel is live for the whole budget

```
┌─ OPERATIONS ───────────────────────────────────────────────────────────────────┐
│  From NL91ABNA0417164300   →   To NL20INGB0001234567                [ Submit ] │
│  Amount   250.00   Rail ‹ instant ›   Key ‹ Generated ›             [ Burst… ] │
│ ────────────────────────────────────────────────────────────────────────────── │
│                                                                                │
│   ~   NO ANSWER YET                          3.1s           [ Cancel payment ] │
│                                                                                │
│       instant · 250.00                                                         │
│       NL91ABNA0417164300 → NL20INGB0001234567                                  │
│       8b21c0aa-51ce-4a85-b524-521275ce337a                                     │
│                                                                                │
│       waiting for the bank to answer                                           │
│                                                                                │
│                                                                                │
│                                                                                │
│                                                       listening since 14:02:11 │
└────────────────────────────────────────────────────────────────────────────────┘
```

### S1c · Omitted mode — the same Cancel label, dimmed, with the reason on screen

```
┌─ OPERATIONS ───────────────────────────────────────────────────────────────────┐
│  From NL91ABNA0417164300   →   To NL20INGB0001234567                [ Submit ] │
│  Amount   250.00   Rail ‹ instant ›   Key ‹ Omitted ›               [ Burst… ] │
│  Omitted mode: not retry-safe after an ambiguous outcome                       │
│ ────────────────────────────────────────────────────────────────────────────── │
│                                                                                │
│   ~   NO ANSWER YET                          3.1s           [ Cancel payment ] │
│                                                                                │
│       instant · 250.00                                                         │
│       NL91ABNA0417164300 → NL20INGB0001234567                                  │
│                                                                                │
│       no transaction id yet — Omitted mode sends no key, so the bank           │
│       names the payment and the console cannot ask for it back                 │
│                                                                                │
│                                                       listening since 14:02:11 │
└────────────────────────────────────────────────────────────────────────────────┘
```

### S2 · One instant payment in flight — the cancel moment

```
┌─ OPERATIONS ───────────────────────────────────────────────────────────────────┐
│  From NL91ABNA0417164300   →   To NL20INGB0001234567                [ Submit ] │
│  Amount   250.00   Rail ‹ instant ›   Key ‹ Generated ›             [ Burst… ] │
│ ────────────────────────────────────────────────────────────────────────────── │
│                                                                                │
│   ~   AWAITING SETTLEMENT                 4.4s              [ Cancel payment ] │
│                                                                                │
│       instant · 250.00                                                         │
│       NL91ABNA0417164300 → NL20INGB0001234567                                  │
│       8b21c0aa-51ce-4a85-b524-521275ce337a                                     │
│                                                                                │
│       submitted ──▶ 202 Pending ──▶ waiting for the bank                       │
│                                                                                │
│                                                                                │
│                                                                                │
│                                                       listening since 14:02:11 │
└────────────────────────────────────────────────────────────────────────────────┘
```

### S3 · Cancel accepted — the bank withdrew it before execution

```
┌─ OPERATIONS ───────────────────────────────────────────────────────────────────┐
│  From NL91ABNA0417164300   →   To NL20INGB0001234567                [ Submit ] │
│  Amount   250.00   Rail ‹ instant ›   Key ‹ Generated ›             [ Burst… ] │
│ ────────────────────────────────────────────────────────────────────────────── │
│                                                                                │
│   ⊘   CANCELLED                           5.1s             [ Look up outcome ] │
│                                                                                │
│       instant · 250.00                                                         │
│       NL91ABNA0417164300 → NL20INGB0001234567                                  │
│       8b21c0aa-51ce-4a85-b524-521275ce337a                                     │
│                                                                                │
│       withdrawn before execution · no money moved                              │
│       safe to retry with a new key                                             │
│                                                                                │
│                                                                                │
│                                                       listening since 14:02:11 │
└────────────────────────────────────────────────────────────────────────────────┘
```

### S2c · The rail withdrew it itself — 504, not an operator cancel

```
┌─ OPERATIONS ───────────────────────────────────────────────────────────────────┐
│  From NL91ABNA0417164300   →   To NL20INGB0001234567                [ Submit ] │
│  Amount   250.00   Rail ‹ instant ›   Key ‹ Generated ›             [ Burst… ] │
│ ────────────────────────────────────────────────────────────────────────────── │
│                                                                                │
│   ⊘   CANCELLED BY THE RAIL                 9.0s           [ Look up outcome ] │
│                                                                                │
│       instant · 250.00                                                         │
│       NL91ABNA0417164300 → NL20INGB0001234567                                  │
│       8b21c0aa-51ce-4a85-b524-521275ce337a                                     │
│                                                                                │
│       the instant rail ran out of time and withdrew it · 504 Cancelled         │
│       no money moved · safe to retry with a new key                            │
│                                                                                │
│                                                                                │
│                                                       listening since 14:02:11 │
└────────────────────────────────────────────────────────────────────────────────┘
```

### S4 · Cancel refused — the bank had already executed it

```
┌─ OPERATIONS ───────────────────────────────────────────────────────────────────┐
│  From NL91ABNA0417164300   →   To NL20INGB0001234567                [ Submit ] │
│  Amount   250.00   Rail ‹ instant ›   Key ‹ Generated ›             [ Burst… ] │
│ ────────────────────────────────────────────────────────────────────────────── │
│                                                                                │
│   ●   SETTLED                             5.1s             [ Look up outcome ] │
│                                                                                │
│       instant · 250.00                                                         │
│       NL91ABNA0417164300 → NL20INGB0001234567                                  │
│       8b21c0aa-51ce-4a85-b524-521275ce337a                                     │
│                                                                                │
│       too late to cancel — the bank had already executed it                    │
│       NL91ABNA0417164300   −250.00  →  4,500.00                                │
│       NL20INGB0001234567   +250.00  →  1,430.00                                │
│                                                                                │
│                                                       listening since 14:02:11 │
└────────────────────────────────────────────────────────────────────────────────┘
```

### S5 · Settled, nothing open — the card owns the screen

```
┌─ OPERATIONS ───────────────────────────────────────────────────────────────────┐
│  From NL91ABNA0417164300   →   To NL20INGB0001234567                [ Submit ] │
│  Amount   250.00   Rail ‹ instant ›   Key ‹ Generated ›             [ Burst… ] │
│ ────────────────────────────────────────────────────────────────────────────── │
│                                                                                │
│   ●   SETTLED                             1.2s             [ Look up outcome ] │
│                                                                                │
│       instant · 250.00                                                         │
│       NL91ABNA0417164300 → NL20INGB0001234567                                  │
│       8b21c0aa-51ce-4a85-b524-521275ce337a                                     │
│                                                                                │
│       NL91ABNA0417164300   −250.00  →  4,750.00                                │
│       NL20INGB0001234567   +250.00  →  1,180.00                                │
│                                                                                │
│                                                                                │
│                                                       listening since 14:02:11 │
└────────────────────────────────────────────────────────────────────────────────┘
```

### S6 · Supplied key mode — a third compose line appears only here

```
┌─ OPERATIONS ───────────────────────────────────────────────────────────────────┐
│  From NL91ABNA0417164300   →   To NL20INGB0001234567                [ Submit ] │
│  Amount   250.00   Rail ‹ instant ›   Key ‹ Supplied ›              [ Burst… ] │
│  Supplied key  demo-key-7f21ab90                                               │
│ ────────────────────────────────────────────────────────────────────────────── │
│                                                                                │
│   ●   SETTLED                             1.2s             [ Resend same key ] │
│                                                                                │
│       instant · 250.00 · key demo-key-7f21ab90                                 │
│       NL91ABNA0417164300 → NL20INGB0001234567                                  │
│                                                                                │
│       NL91ABNA0417164300   −250.00  →  4,750.00                                │
│       NL20INGB0001234567   +250.00  →  1,180.00                                │
│       resent · same key · no second pair of legs                               │
│                                                       listening since 14:02:11 │
└────────────────────────────────────────────────────────────────────────────────┘
```

### S7 · Outage climax — two payments open, the strip appears

```
┌─ OPERATIONS ───────────────────────────────────────────────────────────────────┐
│  From NL91ABNA0417164300   →   To NL20INGB0001234567                [ Submit ] │
│  Amount   250.00   Rail ‹ instant ›   Key ‹ Generated ›             [ Burst… ] │
│ ────────────────────────────────────────────────────────────────────────────── │
│                                                                                │
│   ~   AWAITING SETTLEMENT                12.4s              [ Cancel payment ] │
│                                                                                │
│       instant · 250.00                                                         │
│       NL91ABNA0417164300 → NL20INGB0001234567                                  │
│                                                                                │
│       submitted ──▶ 202 Pending ──▶ CoreBank is down                           │
│                                                                                │
│ STILL OPEN ───────────────────────────────────────── listening since 14:02:11  │
│  ▸ ~ Awaiting  250.00  …0300→…4567  instant    12.4s                           │
│    ~ Awaiting   80.00  …0300→…5264  standard   11.9s                           │
└────────────────────────────────────────────────────────────────────────────────┘
```

### S8 · Burst running — takes over the whole workspace

```
┌─ OPERATIONS ───────────────────────────────────────────────────────────────────┐
│                                                                                │
│ BURST · 200 instant payments · 4 at once ─────────── listening · running 6.2s  │
│                                                                                │
│                                                                                │
│      Sent        200 / 200        accepted 200 · failed 0                      │
│                                                                                │
│      Settled     187              rejected 0 · still moving 13                 │
│                                                                                │
│                                                                                │
│      ██████████████████████████████████████░░░  187 of 200 proven              │
│                                                                                │
│                                                                                │
│                                                                                │
│                                                               [ Stop sending ] │
└────────────────────────────────────────────────────────────────────────────────┘
```

### S9 · Burst drained — holds its result until dismissed

```
┌─ OPERATIONS ───────────────────────────────────────────────────────────────────┐
│                                                                                │
│ BURST · 200 instant payments · 4 at once ──────── listening · drained in 9.8s  │
│                                                                                │
│                                                                                │
│      Sent        200 / 200        accepted 200 · failed 0                      │
│                                                                                │
│      Settled     200              rejected 0 · still moving 0                  │
│                                                                                │
│                                                                                │
│      █████████████████████████████████████████  200 of 200 proven              │
│                                                                                │
│      every payment proved itself · nothing left awaiting                       │
│                                                                                │
│                                                                                │
│                                                                       [ Done ] │
└────────────────────────────────────────────────────────────────────────────────┘
```

### S10 · A refused command — transient line, then it clears

```
┌─ OPERATIONS ───────────────────────────────────────────────────────────────────┐
│  From NL91ABNA0417164300   →   To NL91ABNA0417164300                [ Submit ] │
│  Amount   250.00   Rail ‹ instant ›   Key ‹ Generated ›             [ Burst… ] │
│ ────────────────────────────────────────────────────────────────────────────── │
│                                                                                │
│   ●   SETTLED                             1.2s             [ Look up outcome ] │
│                                                                                │
│       instant · 250.00                                                         │
│       NL91ABNA0417164300 → NL20INGB0001234567                                  │
│                                                                                │
│       NL91ABNA0417164300   −250.00  →  4,750.00                                │
│       NL20INGB0001234567   +250.00  →  1,180.00                                │
│                                                                                │
│  ✕ The source and destination account must differ.                             │
│                                                       listening since 14:02:11 │
└────────────────────────────────────────────────────────────────────────────────┘
```

### S11 · Hard minimum 80×24 — rail collapses to 5, nothing is hidden

```
┌─ OPERATIONS ──────────────────────────────────────────────────────────┐
│ From NL91ABNA0417164300  →  To NL20INGB0001234567          [ Submit ] │
│ Amount 250.00  Rail ‹ instant ›  Key ‹ Generated ›         [ Burst… ] │
│ ───────────────────────────────────────────────────────────────────── │
│                                                                       │
│  ~  AWAITING SETTLEMENT            4.4s            [ Cancel payment ] │
│                                                                       │
│     instant · 250.00                                                  │
│     NL91ABNA0417164300 → NL20INGB0001234567                           │
│     8b21c0aa-51ce-4a85-b524-521275ce337a                              │
│                                                                       │
│     submitted ──▶ 202 Pending ──▶ waiting for the bank                │
│                                                                       │
│                                              listening since 14:02:11 │
└───────────────────────────────────────────────────────────────────────┘
```

