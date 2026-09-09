# STRUCTURE review — DESIGN.md / EXPERIENCE.md (Operations Stage-focus update)

Reviewed: `DESIGN.md` (519 lines), `EXPERIENCE.md` (337 lines), against
`.working/update-brief.md` and `wireframes/operations-stage-focus.md`.
Lens: ownership boundaries, section order, token integrity, component pairing,
restatement, contradiction, override honesty, numbers.

Counts — **1 critical, 7 high, 7 medium, 7 low confirmed; 5 suspicions.**

---

## Confirmed defects

### CRITICAL

**S-01 · Both spines misdescribe the wireframes they are derived from; DESIGN's
column-token derivation rests on the false premise.**
*Files/lines:* `DESIGN.md:431` (Layout & Spacing); `EXPERIENCE.md:89`
(Information Architecture), `EXPERIENCE.md:123` (Component Patterns, Compose
bar), `EXPERIENCE.md:237` (Responsive & Platform).

`DESIGN.md:431` says the column widths "are measured off the approved Stage-focus
wireframes, which are drawn against a 74-cell content line — the width the old
22-cell rail left … The drawings therefore under-draw the real line by six cells."
`EXPERIENCE.md:89` says "They were drawn before the navigation rail narrowed to 16
columns, so they understate the available width by six columns throughout."

Measured: every frame in `wireframes/operations-stage-focus.md` (S1–S10) is **82
characters wide → 80 usable inner columns**, and S11 is **73 wide → 71 inner**.
The wireframe file's own header states "The navigation rail shrinks from 22 columns
to 16, so a 100-column terminal leaves an 82-column content frame and 80 usable
columns" and "the rail collapses to 5 and 71 columns remain." The drawings are at
the **post-update** widths, exactly. They do not under-draw anything.

Three consequences: (a) `DESIGN.md:431`'s whole reserved-column argument ("the six
recovered cells fall to the flexible middle of each line") is reasoning about a
six-cell surplus that does not exist; (b) `EXPERIENCE.md:89` contradicts itself
inside one sentence — "drawn at the real content width" then "understate the
available width by six columns throughout"; (c) `EXPERIENCE.md:123` and `:237`
repeat the same false provenance ("as surely as at the 74 the first wireframes
assumed", "considerably roomier than the first wireframes assumed"), so a reader
who checks the reference finds the spine wrong about it in four places.

*Fix:* delete the six-column under-draw claim everywhere. `DESIGN.md:431` should
read that the wireframes are drawn at the real 80/71 lines and that the reserved
columns were measured against those; `EXPERIENCE.md:89` keeps only "drawn at the
real content width and at the 80×24 floor" and drops the second half of the
sentence; `EXPERIENCE.md:123` becomes "at the 80 usable columns the 16-column rail
leaves"; `EXPERIENCE.md:237` drops "than the first wireframes assumed."
If an *earlier, superseded* set of drawings really was at 74 cells, say that in
one place and stop referring to the linked file as if it were that set.

### HIGH

**S-02 · The still-open strip's visibility rule contradicts itself inside
DESIGN.md, and DESIGN contradicts EXPERIENCE.**
*Files/lines:* `DESIGN.md:429` and `DESIGN.md:437` vs `DESIGN.md:462`,
`EXPERIENCE.md:79`, `:126`, `:241`.

`DESIGN.md:429`: "one row per open payment, and nothing whatsoever when it has
nothing to list." `DESIGN.md:437`: "the still-open strip takes one row per open
payment or none at all." Both rules yield **one row when one payment is open**.
`DESIGN.md:462` says the opposite — "whenever nothing or a single payment is open,
the still-open strip renders nothing at all" — which is the decided rule
(`EXPERIENCE.md:126`: "It renders **only when more than one payment is open**";
`:79`; `:241`; wireframes S2 vs S7). `DESIGN.md:463`'s "**Its zero state renders
nothing at all**" is written as if zero-open were the only collapsed case.

*Fix:* in `:429` and `:437` write "one row per open payment while more than one is
open, and nothing at all — rule included — otherwise"; in `:463` change "Its zero
state" to "Its collapsed state (nothing, or one payment, open)".

**S-03 · Stale "three CloudEvents" claim survives the pass that made the contract
four.**
*File/line:* `EXPERIENCE.md:263` (Inspiration & Anti-patterns).

"**Lifted from the system's own existing broadcast … The three CloudEvents already
exist and PaymentsAPI already consumes them.**" Foundation (`:34`, `:41`, `:43`,
`:45`) now states four types are "the entire contract", and
`CoreBankDemo.ServiceDefaults/CloudEventTypes/TransactionCancelledEvent.cs` exists
in the repo with exactly the fields `:41` claims.

*Fix:* "The four CloudEvents already exist and PaymentsAPI already consumes them."

**S-04 · A workspace-wide rule about the fixed lower action region that no longer
holds everywhere is still asserted unqualified.**
*Files/lines:* `DESIGN.md:475` (Components, Apply faults action) and
`DESIGN.md:492` (Do's and Don'ts).

`:475` — "anchored in the same fixed lower action/detail region as **every other
workspace's primary action**." Operations has no such region
(`DESIGN.md:480`, `EXPERIENCE.md:85`, `:144`), and the *detail* half of that region
was removed from all five workspaces (`DESIGN.md:482`: "what every workspace loses
is the detail band beneath it").
`:492` — the Do column still reads "Keep the primary action in one fixed screen
position per workspace" with no exemption, while `:518` in the same table records
the Operations override. The table therefore both mandates and overrides the rule.

*Fix:* `:475` → "anchored in the same fixed lower action region as the primary
action of Resources and Load Test"; `:492` → "Keep the primary action in one fixed
screen position per workspace (Operations excepted — see the object-anchoring row
below)".

**S-05 · Both files still describe every workspace's content as a list of Activity
rows, which Operations no longer is.**
*Files/lines:* `EXPERIENCE.md:83` (Information Architecture, Main workspace
composition) and `DESIGN.md:457` (Components, Activity row).

`EXPERIENCE.md:83`: "**Faults is the one workspace** whose content is a fixed set
of labelled controls rather than a scrolling row list … Content is a list of
`DESIGN.md` **Activity rows**." Operations' content is now a compose bar, a focus
card and one-line strip rows that are explicitly "not an **Activity row**'s two"
(`DESIGN.md:463`) — so Faults is no longer the one exception and Operations' main
content is not an Activity-row list.
`DESIGN.md:457` carries the same stale premise: "the atomic unit of **every**
workspace's main content (resource rows, payment/evidence entries, invariant
chips)", qualified only by the empty-placeholder exception.

*Fix:* `EXPERIENCE.md:83` — "Resources, Evidence/Results and Load Test render as
lists of Activity rows; Faults is a fixed set of labelled controls and Operations
is the three-region Stage-focus layout (see above); all obey the same borderless,
one-tone rules." `DESIGN.md:457` — scope the claim to the three row-list
workspaces.

**S-06 · Reference to a surface this update deleted.**
*File/line:* `DESIGN.md:459` (Components, Event row).

"used in the Evidence/Results event feed and, condensed, **beneath the originating
payment row in Operations**." Operations has no payment rows any more; the
condensed event is the focus card's closing block (`EXPERIENCE.md:142`: "condensed
on the focus card in Operations, as that payment's closing block";
`DESIGN.md:462`).

*Fix:* "…and, condensed, as the closing block of the Operations focus card."

**S-07 · The "no history in Operations / the strip collapses so the card owns the
screen" argument is made four times, twice of them in the wrong file.**
*Files/lines:* `EXPERIENCE.md:79` (canonical home, "Why Operations shows no
history"), `EXPERIENCE.md:126`, `DESIGN.md:462`, `DESIGN.md:463`.

`EXPERIENCE.md:79`: "that collapse is the point: it is what lets a settled focus
card own the entire content area … Duplicating the session's history in Operations
was the space cost the operator objected to."
`EXPERIENCE.md:126` re-argues it: "which is what lets a settled card own the whole
surface … It is not a history and must never become one … the session record lives
in the Evidence workspace."
`DESIGN.md:463` re-argues it a third time and in the behaviour file's territory:
"a `No payments awaiting` row would spend rows of the payment area restating what
the card just said. … **Session history is not rendered in Operations at all; it
lives in the Evidence workspace, one keypress away.**" — with no cross-reference.
`DESIGN.md:462` makes the card-owns-the-screen half a fourth time.

*Fix:* keep the argument only at `EXPERIENCE.md:79`. `EXPERIENCE.md:126` states the
rule and points there. `DESIGN.md:463` keeps only what is visual (zero-height,
rule included, no placeholder row, the Activity-row exception) and replaces the
history sentence with "(rationale: `EXPERIENCE.md` Information Architecture, *Why
Operations shows no history*)". `DESIGN.md:462` keeps the *visual* consequence
(the card must look finished alone) and drops the re-argument.

**S-08 · The bottom-band-removal argument is made in five places across the two
files.**
*Files/lines:* `EXPERIENCE.md:63` (canonical home, "Override — the bottom band is
removed"), `EXPERIENCE.md:61`, `EXPERIENCE.md:146`, `EXPERIENCE.md:225`,
`DESIGN.md:482`, `DESIGN.md:470`.

The identical three-part argument — *the strip was never the record; the durable
record is in Evidence; "silence is never an outcome" is untouched* — appears at
`EXPERIENCE.md:63`, again in the table row immediately above it (`:61`, "must not
spend a permanent row in all five workspaces to be ready for it"), again at
`:146` and `:225`, and twice in DESIGN (`:482` "What replaces the strip is not less
honest, only less permanent … The invariant the strip was thought to be carrying …
is untouched"; `:470` "**It is an announcement, never a record.** Everything it can
say is written durably in the Evidence workspace first"). `DESIGN.md:482` also
duplicates `EXPERIENCE.md:63`'s uniformity argument ("the shell is more uniform
without them, not less" / "deliberately uniform across all five workspaces").

*Fix:* one home at `EXPERIENCE.md:63`. `:61` states the placement only. `:146` and
`:225` cross-reference. `DESIGN.md:482` keeps the token/row consequences (the
component and its group are deleted; `{spacing.shell-chrome-height}` is 7 of 30)
and cross-references the rest. `DESIGN.md:470` keeps the visual spec and its
"see `EXPERIENCE.md`" pointer without re-arguing.

### MEDIUM

**S-09 · "Exactly two places … no third place" for the feed status is false against
EXPERIENCE.**
*Files/lines:* `DESIGN.md:456` vs `EXPERIENCE.md:141`, `:180`, `:245`.

`DESIGN.md:456`: the inline feed status "appears in exactly two places: once at the
foot of the Operations content area … and as the Evidence feed's header line …
There is still no third place and no chip." But `EXPERIENCE.md:141` says the
override of the per-row `(listening)` qualifier is "in Operations only —
Evidence/Results keeps its header **and its inline form**", `:180` says "each
awaiting row carries `(listening)` inline", and `:245` says outside Operations that
qualifier is the last thing a row gives up. That is a third place, and it is the
same component.

*Fix:* `DESIGN.md:456` → "three places: the foot of the Operations content area or
the STILL OPEN rule, the Evidence feed header, and inline on an Evidence awaiting
row" (and keep "never a chip").

**S-10 · EXPERIENCE re-derives DESIGN's rail-width arithmetic instead of citing the
token.**
*Files/lines:* `EXPERIENCE.md:236` (and `:123`) vs `DESIGN.md:427`.

`EXPERIENCE.md:236`: "narrowing the navigation rail from 22 columns to 16 in its
preferred state (`{spacing.nav-rail-width}` — its five labels, longest `Load Test`,
need 14 columns plus 2 of border)". The derivation of a spacing token's value is
`DESIGN.md:427`'s job and is stated there in the same words. Both the ownership
line and the argue-once rule are crossed.

*Fix:* `EXPERIENCE.md:236` keeps the behavioural consequence ("the rail at
`{spacing.nav-rail-width}` returns six columns to every workspace; inner width 80
at 100 columns — see `DESIGN.md` Layout & Spacing for the derivation") and drops
the label arithmetic. Same treatment for the "as surely as at the 74" clause in
`:123`.

**S-11 · Behaviour specified in DESIGN: the burst takeover's lifetime and its
action-slot labels.**
*File/line:* `DESIGN.md:468` vs `EXPERIENCE.md:130`.

"while a burst is running, **and while its drained result is still held** … it is
the **stop control while sending and the dismiss control once drained**" — when the
takeover appears, how long it holds, and which label the slot carries are
behaviour, owned verbatim by `EXPERIENCE.md:130`.

*Fix:* DESIGN keeps "the slot never holds two meanings at once and both labels land
in the same cells (see `EXPERIENCE.md` Component Patterns, Burst takeover for when
each is shown)".

**S-12 · Behaviour specified in DESIGN: the currency wire contract.**
*File/line:* `DESIGN.md:461` vs `EXPERIENCE.md:123`.

"Currency stopped being an operator input … **the wire still carries a fixed
`EUR`**." What the console sends is behaviour; `EXPERIENCE.md:123` owns it
(including the pending-confirmation assumption, which DESIGN silently drops).

*Fix:* DESIGN keeps "**There is no currency field**" and the visual reason (a
caption beside an unchangeable value is chrome), and cross-references
`EXPERIENCE.md` Component Patterns, Compose bar for what goes on the wire.

**S-13 · The cancel-has-no-confirmation justification is argued in three places.**
*Files/lines:* `EXPERIENCE.md:199` (canonical home, Interaction Primitives),
`EXPERIENCE.md:127`, `DESIGN.md:464`.

`:127` gives the reason *and* the pointer ("because a cancelled payment moved no
money and is safe to resubmit under a new key (see Interaction Primitives for why
that is consistent rather than an exception)"). `DESIGN.md:464` gives the same
argument a third time in the visual file ("a cancelled payment moved no money and
is safe to retry with a new key, so the double-bordered red treatment would spend
this console's one data-loss warning on something that loses nothing — the same
argument that keeps the **Apply faults action** off those tokens"), which is also
`EXPERIENCE.md:199`'s Apply/panic-off parallel.

*Fix:* `:127` states the rule ("fires immediately — no confirmation modal, no `Y`")
and points to `:199` without repeating the reason. `DESIGN.md:464` argues only the
token choice (why not `destructive-action-button` tokens) and cites
`EXPERIENCE.md` Interaction Primitives for the confirmation decision itself.

**S-14 · The removed "detail" band is still in the name of the surviving region.**
*Files/lines:* `EXPERIENCE.md:85`, `:144`, `:192`, `:253`; `DESIGN.md:475`.

`DESIGN.md:482` is explicit: "Resources, Faults and Load Test keep their fixed
lower **action** region; what every workspace loses is the **detail** band beneath
it." Four EXPERIENCE sites and one DESIGN site still call it the "fixed lower
action/**detail** region", so the shell reads as still having a detail band in
three workspaces.

*Fix:* global rename to "fixed lower action region" at those five sites.

**S-15 · A visual value specified in EXPERIENCE rather than referenced.**
*File/line:* `EXPERIENCE.md:125` (Component Patterns, Focus card).

"Top line: the state symbol, the state word **in upper case**, the elapsed clock…"
Casing is a `DESIGN.md` token (`components.focus-card.state-word-case: upper`) and
an argued emphasis channel (`DESIGN.md:421`). `EXPERIENCE.md:224` repeats it.

*Fix:* "the state word (cased and weighted per `DESIGN.md` Focus card)". Keep the
behavioural half — that the word is never abbreviated to fit (`:224`).

### LOW

**S-16 · Dead spacing tokens.** `spacing.'0'` (`0ch`) and `spacing.'2'` (`2ch`)
(`DESIGN.md:67`, `:69`) are never referenced: the body cites only the range
"`{spacing.1}`–`{spacing.3}`" (`DESIGN.md:425`) and no component field uses either.
*Fix:* either reference them in the scale sentence explicitly or drop them.
(Pre-existing, not introduced by this pass.)

**S-17 · An override that does not name what it replaces.** `DESIGN.md:463`:
"**supersedes the EARLIER history list** the first Stage-focus drafts placed here."
"EARLIER" in caps is an artifact of the update brief's revision marker
(`.working/update-brief.md:20`, `:38`), not a component name; the superseded object
is unnamed. *Fix:* "supersedes the session-history list the first Stage-focus drafts
placed here."

**S-18 · Stale `sources:` frontmatter.** Both files list the three CloudEvent
contract files (`DESIGN.md:17-20`, `EXPERIENCE.md:16-19`) but not
`CoreBankDemo.ServiceDefaults/CloudEventTypes/TransactionCancelledEvent.cs`, which
exists and is now load-bearing for `EXPERIENCE.md:41`. *Fix:* add it to both lists.

**S-19 · DESIGN cites the wireframes without linking them.** `DESIGN.md:431`
("the approved Stage-focus wireframes") has no path; `EXPERIENCE.md:89` links
`wireframes/operations-stage-focus.md`. *Fix:* add the same relative link.

**S-20 · Do's and Don'ts row contradicts the three-role teal rule.**
`DESIGN.md:493` "Use `accent-teal` only for the primary action and focus ring" vs
`:502` "Reserve `accent-teal` for its three stated roles — primary action, focus
ring, lock-exempt/faults-in-force" and the Colors section (`:366`). Pre-existing.
*Fix:* make `:493` name the third role or delete the row as subsumed by `:502`.

**S-21 · Lock-exempt membership counted differently in the two files.**
`DESIGN.md:471` and `:464` — "exactly three controls … burst Cancel, every fault
slider, and panic-off", "stays at exactly three members". `EXPERIENCE.md:169`
category 3 — "**every control in the Faults workspace**, plus the global panic-off
key", which includes the preset chips and Apply. Pre-existing, but the update leans
on the "exactly three" number to argue Cancel's treatment. *Fix:* reconcile — either
DESIGN says "the burst Cancel plus every Faults-workspace control and panic-off",
or `EXPERIENCE.md:169` narrows to the sliders.

**S-22 · "There is no separate projector theme" survives the light palette.**
`EXPERIENCE.md:248` (Projector mode) — "there is no separate 'projector theme', the
same default surface is legible at projector distance per `DESIGN.md`
Colors/Typography" — while `DESIGN.md:386-392` introduces a second palette whose
stated purpose is "presenting on a projector". Pre-existing (light mode predates
this pass), and unresolved. *Fix:* "there is no projector-only *layout*; the light
palette (`DESIGN.md` Light mode) is the projector affordance and is a token swap
only."

---

## Suspicions (not confirmed defects)

1. **Wireframe row fidelity.** The frames are 16 rows tall at the preferred size
   (14 inner) and 15 at the floor (13 inner), against a stated budget of 23 content
   rows (7 chrome + 3 compose + 20 payment area, `DESIGN.md:437`,
   `EXPERIENCE.md:236`). Neither file claims *row* fidelity for the drawings — both
   are careful to say "content **width**" — so this may be deliberate. But the
   20-row payment area is evidenced by nothing, and the drawings show ~11.
2. **Compose bar row budget.** `DESIGN.md:437` allots "3 … to the compose bar and
   its rule" flatly, while `compose-bar-height-max` is `3row` in Supplied/Omitted
   mode, making it 4 with the rule and the payment area 19.
   `EXPERIENCE.md:241` handles this ("costs a row only in the mode that needs it");
   DESIGN's budget sentence does not acknowledge it.
3. **Source materials disagree about the strip threshold.**
   `.working/update-brief.md:24` says the strip "collapses to zero rows … when
   **nothing** is open"; `wireframes/operations-stage-focus.md:13` says it "appears
   only when **more than one** payment is open". The spines follow the wireframe
   doc. Worth confirming with the operator which is decided, and correcting the
   brief — S-02's fix assumes the wireframe reading.
4. **Borderline behaviour in DESIGN.** `:388` (palette selected with `--light`,
   `COREBANK_DEMO_THEME`, or `T`), `:456` ("when the feed is lost the payment rows
   change **state** rather than gaining a badge"), `:469` ("never dims or disables
   during the single-action-in-flight lock"). All three cross-reference
   `EXPERIENCE.md` and all three predate this pass; each states behaviour in the
   visual file, and each could be trimmed to its visual half.
5. **Hard-coded symbols in EXPERIENCE.** `:164`/`:165` print `⊘ CANCELLED` and
   `● SETTLED` directly, where `:167` shows the disciplined form ("`DESIGN.md` is
   where the token is fixed … the constraint this file imposes is that it must not
   be the failure token"). Consistent with the file's long-standing use of
   `●`/`~`/`○`/`✕` in microcopy, so likely deliberate rather than a new leak.

---

## Sections found clean

- **Section order and completeness — both files, clean.** `DESIGN.md` runs Brand &
  Style (352), Colors (360, with Light mode 386), Typography (417), Layout &
  Spacing (423), Elevation & Depth (439), Shapes (445), Components (449), Do's and
  Don'ts (485) — exact required order, nothing missing, nothing extra.
  `EXPERIENCE.md` runs Foundation (26), Information Architecture (51), Voice and
  Tone (91), Component Patterns (111), State Patterns (149), Interaction
  Primitives (189), Accessibility Floor (212), Responsive & Platform (230),
  Inspiration & Anti-patterns (251), Key Flows (273) — exact required order.
- **Token integrity — clean apart from S-16.** All `{group.name}` references in
  frontmatter and body resolve; **zero dangling references**. `colors` and
  `colors-light` hold **identical 16-key sets**. All 34 component groups are
  referenced from the body; all 30 spacing tokens are referenced except `'0'` and
  `'2'` (S-16); `event-gutter-width` and `settlement-leg-column` are referenced from
  component fields rather than prose, which is legitimate. Every token added by
  this pass (compose-bar-*, focus-card-*, still-open-selection-gutter, burst-*,
  shell-chrome-height, elapsed-clock-column, cancel-payment-action,
  still-open-strip, burst-takeover, transient-announcement) is both declared and
  consumed.
- **Component pairing — no new breaks.** Every DESIGN component has a same-named
  EXPERIENCE Component Patterns row and vice versa, including all six new/renamed
  entries (Compose bar, Focus card, Still-open strip, Cancel payment action, Burst
  takeover, Transient announcement) and the renames (Payment form → Compose bar,
  DevProxy toggle → Fault arming toggle, both recorded). The only mismatches are
  the three declared pre-existing ones (Feed status punctuation, Lock-exempt
  control signature, Fault chip / Settlement legs).
- **Numbers (item 8) — clean.** Every figure is consistent with 16-column rail
  (`DESIGN.md:74`, `:427`, `:454`; `EXPERIENCE.md:89`, `:123`, `:236`), 7-row shell
  chrome (`DESIGN.md:92`, `:437`, `:482`; `EXPERIENCE.md:236`), 80 inner columns at
  100 and 71 at the 80-column floor (`DESIGN.md:427`, `:431`, `:437`;
  `EXPERIENCE.md:236`, `:237`, `:242`). Row budget adds up (7+3+20 = 30; 23 content
  rows = 3 + 20). Rail arithmetic checks out in both states (100−16 = 84 frame → 80
  inner; 80−5 = 75 frame → 71 inner). Every occurrence of 22, 74, 10 rows and
  "sixteen-row" is framed historically. The only width falsehood is S-01, which is
  about the drawings' provenance, not the budget.
- **Override honesty — clean.** Every overturned decision names what it replaces:
  `DESIGN.md:356` (no dark/light split), `:427` (the earlier `14ch`/unstated 22),
  `:456` (the per-row `(listening)` parenthetical), `:461` (Payment form), `:466`
  (Resend's primary-action tokens), `:469` (the "outcome key" field), `:480` (the
  one-fixed-slot rule), `:482` (Evidence strip, key legend, in-flight line);
  `EXPERIENCE.md:34` (three→four events), `:63` (the two removed chrome rows),
  `:85` (both halves of the primary-action rule), `:89` (the no-wireframe claim),
  `:123` (Payment form), `:129` (HTTP leg / proven leg), `:131` (Outcome key),
  `:141` (per-row qualifier, Operations only), `:146` (Evidence strip), `:241` (the
  previous degradation order). Only S-17 (an unnamed superseded object) falls short.
- `DESIGN.md` Typography, Elevation & Depth and Shapes carry no behavioural leak
  and no stale figure.
