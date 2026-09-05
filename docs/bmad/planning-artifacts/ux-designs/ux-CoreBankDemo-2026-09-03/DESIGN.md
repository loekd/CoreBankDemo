---
name: CoreBankDemo DemoRunner
description: Reusable terminal operator console for live resilience demonstrations.
status: final
sources:
  - ../../briefs/brief-demorunner-console-2026-09-03/brief.md
  - ../../../implementation-artifacts/spec-7-4-reusable-terminal-demo-operator-console.md
  - ../../epics.md
  - ../../../../adr/ADR-015-presentation-safe-terminal-demo-console.md
  - ../../../../adr/ADR-018-instant-payment-rail.md
  - ../../../implementation-artifacts/spec-add-instant-payment-rail.md
  - ../../../implementation-artifacts/spec-add-instant-rail-load-coverage.md
  - ../../../../../CoreBankDemo.DemoRunner/Scenarios/mission-critical-talk-v7.json
  - ../../../../../CoreBankDemo.AppHost/devproxy/config/devproxyrc.json
  - ../../../../../CoreBankDemo.AppHost/devproxy/config/devproxy-errors.json
  - ../../../../../CoreBankDemo.LoadTests/devproxy/config/devproxyrc-latency.json
  - ../../../../../README.md
updated: 2026-09-05
colors:
  surface-base: '#0B1220'
  surface-raised: '#132036'
  surface-overlay: '#182A44'
  text-primary: '#E8ECF1'
  text-secondary: '#9AA7B8'
  text-muted: '#7C8CA0'
  border-hairline: '#2A3A50'
  accent-teal: '#2FB7A8'
  accent-navy: '#325F8C'
  state-healthy: '#3FBF63'
  state-running: '#D8A93B'
  state-neutral: '#9AA7B8'
  state-ambiguous: '#D8A93B'
  state-failed: '#D9534F'
  state-failed-on-raised: '#E06862'
typography:
  label:
    note: 'Terminal monospace inherited from the host emulator; bold attribute — pane headers, resource names, primary action captions'
  body:
    note: 'Terminal monospace, normal attribute — table rows, form fields, evidence prose'
  meta:
    note: 'Terminal monospace, dim/secondary attribute — timestamps, ids, latency figures, footnotes'
  evidence-mono:
    note: 'Terminal monospace, unstyled, no line-wrap collapsing by default — raw HTTP/JSON bodies and log lines, always fixed-width for byte-for-byte fidelity. Long lines pan horizontally (arrow keys / Shift+←/→) with a "more →" indicator; an explicit opt-in wrap toggle re-flows the same underlying bytes into a soft-wrapped read view without altering them — the unwrapped view remains the default and the raw bytes are identical either way.'
spacing:
  '0': '0ch'
  '1': '1ch'
  '2': '2ch'
  '3': '3ch'
  gutter: '1ch'
  pane-margin: '1ch'
  section-gap: '1row'
  nav-rail-width: '14ch'
  nav-rail-width-compact: '4ch'
  fault-slider-track-width: '24ch'
  fault-slider-track-min: '10ch'
  fault-value-column: '14ch'
  preferred-width: '100ch'
  preferred-height: '30row'
  hard-min-width: '80ch'
  hard-min-height: '24row'
components:
  topology-bar:
    background: '{colors.surface-base}'
    foreground: '{colors.text-primary}'
    border: 'single-hairline-bottom'
    border-color: '{colors.border-hairline}'
  nav-rail:
    background: '{colors.surface-raised}'
    foreground: '{colors.text-primary}'
    width: '{spacing.nav-rail-width}'
    border: 'single-hairline-right'
    border-color: '{colors.border-hairline}'
  nav-rail-item:
    background-active: '{colors.accent-navy}'
    background-inactive: '{colors.surface-raised}'
    foreground: '{colors.text-primary}'
  status-chip-healthy:
    foreground: '{colors.state-healthy}'
    symbol: '●'
  status-chip-running:
    foreground: '{colors.state-running}'
    symbol: '~'
    fallback-symbol: '[~]'
  status-chip-unknown:
    foreground: '{colors.state-neutral}'
    symbol: '○'
  status-chip-failed:
    foreground: '{colors.state-failed}'
    symbol: '✕'
  status-chip-fault:
    foreground-in-force: '{colors.accent-teal}'
    foreground-armed: '{colors.text-secondary}'
    foreground-unavailable: '{colors.text-muted}'
    symbol-in-force: '!'
    symbol-armed: '·'
    symbol-unavailable: '-'
  primary-action-button:
    background: '{colors.accent-teal}'
    foreground: '{colors.surface-base}'
    border: 'single'
    focus-style: 'reverse-video'
  destructive-action-button:
    background: '{colors.surface-raised}'
    foreground: '{colors.state-failed-on-raised}'
    border: 'double'
    confirm-style: 'typed-confirmation-key-Y'
  activity-row:
    background: '{colors.surface-base}'
    foreground-headline: '{colors.text-primary}'
    foreground-meta: '{colors.text-muted}'
    gutter-width: '{spacing.gutter}'
    border: 'none'
  resource-row:
    extends: '{components.activity-row}'
    action-slot-width: '12ch'
  apphost-control-panel:
    background: '{colors.surface-base}'
    foreground: '{colors.text-primary}'
    badge-owned: '{colors.state-healthy}'
    badge-attached: '{colors.state-neutral}'
    border: 'none'
  payment-form:
    background: '{colors.surface-base}'
    foreground-label: '{colors.text-primary}'
    foreground-value: '{colors.text-secondary}'
    rail-chip-selected: '{colors.accent-navy}'
    rail-chip-unselected: '{colors.surface-raised}'
    border: 'none'
  idempotency-selector:
    chip-selected-background: '{colors.accent-navy}'
    chip-selected-foreground: '{colors.text-primary}'
    chip-unselected-background: '{colors.surface-raised}'
    chip-unselected-foreground: '{colors.text-secondary}'
    warning-foreground: '{colors.state-ambiguous}'
  resend-same-key-action:
    enabled: '{components.primary-action-button}'
    disabled-foreground: '{colors.text-muted}'
    disabled-fill: 'none'
  burst-control:
    background: '{colors.surface-base}'
    counter-accepted: '{colors.state-healthy}'
    counter-completed: '{colors.state-healthy}'
    counter-failed: '{colors.state-failed}'
    cancel-border: 'single'
    cancel-foreground: '{colors.accent-teal}'
    cancel-fill: 'none'
  outcome-query:
    background: '{colors.surface-base}'
    foreground: '{colors.text-primary}'
    always-enabled: true
  replica-indicator:
    foreground: '{colors.text-secondary}'
    stepper-border: 'single'
    stepper-foreground: '{colors.accent-teal}'
  load-phase-strip:
    background: '{colors.surface-base}'
    cell-not-yet-reached: '{colors.state-neutral}'
    cell-active: '{colors.state-running}'
    cell-passed: '{colors.state-healthy}'
    cell-failed: '{colors.state-failed}'
    divider: 'single-hairline'
  invariant-chip:
    background: '{colors.surface-base}'
    border: 'single'
    foreground-pass: '{colors.state-healthy}'
    foreground-fail: '{colors.state-failed}'
    foreground-not-yet-observed: '{colors.state-neutral}'
  evidence-strip:
    background: '{colors.surface-base}'
    foreground: '{colors.text-secondary}'
    border: 'single-hairline-top'
    border-color: '{colors.border-hairline}'
  fault-slider:
    background: '{colors.surface-base}'
    label: '{colors.text-primary}'
    value: '{colors.text-secondary}'
    track: '{colors.border-hairline}'
    fill-live: '{colors.accent-teal}'
    fill-staged: '{colors.text-secondary}'
    handle: '{colors.text-primary}'
    handle-focus-style: 'reverse-video'
    disabled-foreground: '{colors.text-muted}'
    track-width: '{spacing.fault-slider-track-width}'
    value-column: '{spacing.fault-value-column}'
    border: 'none'
    lock-exempt: true
  fault-preset-chip:
    chip-selected-background: '{colors.accent-navy}'
    chip-selected-foreground: '{colors.text-primary}'
    chip-unselected-background: '{colors.surface-raised}'
    chip-unselected-foreground: '{colors.text-secondary}'
    custom-label-foreground: '{colors.text-secondary}'
  fault-arming-toggle:
    armed-background: '{colors.accent-teal}'
    armed-foreground: '{colors.surface-base}'
    unarmed-background: '{colors.surface-raised}'
    unarmed-foreground: '{colors.text-secondary}'
    unavailable-foreground: '{colors.text-muted}'
  apply-faults-button:
    extends: '{components.primary-action-button}'
    staged-delta-foreground: '{colors.text-secondary}'
    disabled-foreground: '{colors.text-muted}'
    disabled-fill: 'none'
  panic-off-control:
    border: 'single'
    foreground: '{colors.accent-teal}'
    fill: 'none'
    always-enabled: true
    lock-exempt: true
    key: '0'
  confirmation-modal:
    background: '{colors.surface-overlay}'
    foreground: '{colors.text-primary}'
    border: 'double'
    scrim: '{colors.surface-base}'
    focus-on-open: 'cancel'
    confirm-key: 'Y'
    cancel-key: 'Escape'
---

## Brand & Style

This is a **Terminal.Gui** cockpit, not a web or mobile surface — there is no CSS box model, no font-loading, no hover state, and color rendering depends entirely on the host terminal's capability (24-bit truecolor down to a 16-color ANSI palette). Every token below is written as a hex value for a truecolor terminal; the **Colors** section states the 16-color fallback mapping in prose, because the frontmatter spec fixes color values to hex strings.

The retired `mission-critical-talk-v7.json` cue player leaned on the visual language of the author's deck — a restrained teal-on-navy control-room palette. That posture is preserved **only** where it does not compete with the harder requirement: legibility at projector distance and truthful state that never depends on color perception alone. Where the two pull apart, legibility and text/symbol redundancy win. `[ASSUMPTION]` The exact teal/navy hex values below are a reasonable interpretation of "restrained cockpit language," not a value confirmed against the actual deck; `[ASSUMPTION]` no dark/light mode split is modeled — the console has one dark cockpit surface, matching the brief's "no i18n, no dark/light theming beyond legibility."

**Composition reference:** [`imports/operator-console-inspiration.png`](imports/operator-console-inspiration.png) is a **user-supplied operator-console composition reference** (not a Docker Sandboxes screenshot — see `.memlog.md`'s rename decision), used **only** for the composition principles listed once in `EXPERIENCE.md` Inspiration & Anti-patterns (cross-referenced from here rather than restated). None of its branding, exact colors, labels, or literal layout are carried over; every principle is re-expressed in this console's own restrained teal/navy cockpit language and Terminal.Gui constraints. Where the reference and this spine's stated requirements conflict, **this spine (and its paired `EXPERIENCE.md`) wins**.

## Colors

- **Surface Base (`{colors.surface-base}`)** — the console's canvas: deep navy, near-black. It is now the **single, uninterrupted work surface** — topology bar, every workspace's content, and the lower action/detail band all sit on this one tone, divided only by hairline rules, never by a separate raised "panel" background. Never used for text.
- **Surface Raised (`{colors.surface-raised}`)** — one step up in tone from the base, deliberately reserved for exactly one region: the persistent navigation rail and its item states. That restriction is what makes the rail read as *chrome you navigate with*, distinct at a glance from the near-black canvas you read and act on — it is no longer used for boxed workspace panels. **Surface Overlay (`{colors.surface-overlay}`)** — two steps up, used only for the confirmation modal. Terminal has no shadow, so tone-stepping is the only depth cue (see **Elevation & Depth**).
- **Text Primary (`{colors.text-primary}`)**, **Text Secondary (`{colors.text-secondary}`)**, **Text Muted (`{colors.text-muted}`)** — a three-step readability ramp: primary content, secondary/meta content (timestamps, ids), and disabled/inactive labels. All three clear WCAG AA normal-text contrast (≥4.5:1) against both `surface-base` and `surface-raised`, the only two backgrounds any of the three is rendered on — this is the accessibility floor's non-negotiable line (see `EXPERIENCE.md` Accessibility Floor). Computed ratios: `text-primary` 15.78:1 on `surface-base`, 13.75:1 on `surface-raised`; `text-secondary` 7.66:1 on `surface-base`, 6.67:1 on `surface-raised`; `text-muted` 5.45:1 on `surface-base`, 4.75:1 on `surface-raised`. `text-muted`'s hex was raised from an earlier `#5B6B80` (which measured 3.44:1/3.00:1 and failed AA) to `#7C8CA0` specifically to clear the 4.5:1 floor on `surface-raised`, the tighter of the two backgrounds, while staying visibly dimmer than `text-secondary`.
- **Border Hairline (`{colors.border-hairline}`)** — the low-emphasis divider used by the topology bar, navigation rail, and evidence strip. It separates persistent chrome without boxing the workspace into panels; it never carries state.
- **Accent Teal (`{colors.accent-teal}`)** — the single restrained chromatic accent inherited from the deck's cockpit language. It has exactly **three** roles and no others: the primary action button, the focused-control indicator, and the **lock-exempt / faults-in-force signature** (see below). Never used for a pass/fail state badge, never for chrome, never decoratively — this mirrors the "one accent, used sparingly" discipline that other DESIGN.md examples in this project's toolchain use, adapted here to a control surface rather than a writing surface. `surface-base`-on-`accent-teal` (the primary-action-button's own text/fill pairing) is 7.54:1, well clear of AA; `accent-teal` as a foreground on `surface-base` (fault chip, lock-exempt outlines, slider live fill) is the same 7.54:1, since contrast is symmetric.
  - **Why faults get the third role.** Fault injection is neither a proven pass, a proven failure, nor an awaited proof, so it cannot borrow the `state-*` palette without lying: green would assert a health nothing measured, red a failure that has not happened, amber an outcome still pending. Teal is the only vocabulary left that says *"this is a live thing you did"* rather than *"this is a verdict the system reached."* The same reasoning gives teal to the lock-exempt controls (burst Cancel, fault sliders, panic-off): those, too, state what the operator may do right now, never what the system proved.
- **Fault severity is deliberately colorless.** No token maps to "how bad" a fault level is, and none should be added. Severity is carried by the numeric value, the bar fill length, and the text label; the only thing color says about faults is the binary in-force-or-not. A green-amber-red severity ramp would collide head-on with the `state-*` meanings above — "5% errors" rendered green would read as a proven-healthy badge to every operator who has learned this palette anywhere else in the console.
- **Accent Navy (`{colors.accent-navy}`)** — the structural/secondary accent: active navigation-rail item background, topology-bar structural elements. Distinct from `accent-teal` so "the thing you can press right now" (teal) is never visually confused with "the section you are currently in" (navy). `text-primary`-on-`accent-navy` (the active nav-rail-item label) is 5.62:1; the token's original `#3B6EA5` measured only 4.47:1 there and was darkened to `#325F8C` to clear AA with headroom.
- **State colors — `state-healthy`, `state-running`, `state-neutral`, `state-ambiguous`, `state-failed`, `state-failed-on-raised`.** These carry all pass/fail/unknown meaning in the console and are the one place this DESIGN.md is load-bearing rather than decorative:
  - **`state-failed` (red) is reserved for a proven failure** — an assertion that ran and did not pass, an HTTP error, a resource transition Aspire itself reports as failed. It is never used speculatively. On `surface-base` (status chips, activity-row gutters) it measures 4.73:1, clearing AA.
  - **`state-failed-on-raised`** is a lightened tint (`#E06862`, 4.91:1 on `surface-raised`) used *only* for the destructive-action-button's label, because the base `state-failed` hex measures a sub-AA 4.12:1 on `surface-raised` — not close enough to round up. Both reds read as the same "danger" hue at a glance; only the on-`surface-raised` pairing uses the lightened tint, and only because that is the one place `state-failed` sits on `surface-raised` as normal-weight text that a presenter must not misread.
  - **`state-healthy` (green) is reserved for a proven pass or a confirmed-healthy resource** — never for "probably fine" or "still waiting." 7.88:1 on `surface-base`.
  - **`state-running` and `state-ambiguous` (the same amber)** cover every in-between truth: in flight, unknown, awaiting reconciliation, `202 Pending`. Collapsing "running" and "ambiguous" into one amber family is deliberate: both mean "no proof yet," and the accompanying text/symbol (see below) is what distinguishes them, not the color. 8.61:1 on `surface-base`.
  - **`state-neutral` (the same tone as `text-secondary`)** marks resources whose state has not yet been read (cold start, stale snapshot) — genuinely unknown, not merely pending.
  - Every one of these is paired with a fixed symbol (`●` healthy, `~` running/ambiguous, `○` unknown, `✕` failed) and a text label in every rendering; **color is never the sole channel**, satisfying 16-color and color-vision-deficient terminals alike. The running/ambiguous symbol changed from the earlier `◐` (U+25D0, half-filled circle) to the ASCII `~` because `◐` renders inconsistently across monospace terminal fonts (some substitute a box/tofu glyph) and is the hardest of the four to distinguish at projector distance, while `~` is a plain ASCII character (0x7E) guaranteed to render identically everywhere — and because this is explicitly the state the brief cares most about getting right ("nine seconds must look like it is working, not like it hung"), the text label (`Running`/`Ambiguous`/`Pending`) remains the *primary* discriminator in every rendering; the symbol is secondary reinforcement, never the sole cue, for this state specifically. **Monochrome/ASCII-only fallback:** on a terminal that cannot render any of the four symbols (a legacy ASCII-only console, no Unicode box-drawing support), the resolver substitutes bracketed ASCII markers — `[#]` healthy, `[~]` running/ambiguous, `[ ]` unknown, `[X]` failed — with the text label unchanged and still primary; `~` alone needs no such fallback since it is already ASCII.
  - **Fault chip** (`{components.status-chip-fault}`) is a sixth, independent chip — not a pass/fail state — always present in the topology bar. It has three states, each rendered as symbol + label + color and never color alone. `!`/`accent-teal` labeled `Faults in force` means a level is applied *and* has been observed in traffic; `·`/`text-secondary` labeled `Armed` means a Dev Proxy is running but every knob is zero, because an armed proxy injecting nothing must never look like an active fault; `-`/`text-muted` labeled `Unavailable` means this topology started unarmed, or is Attached. The symbol set is plain ASCII (`!`, `·` U+00B7 with `.` as its ASCII fallback, `-`) rather than the earlier `⚡`, which is an emoji-class glyph that renders at inconsistent width across monospace terminals and frequently as tofu — the same reasoning that moved the running state from `◐` to `~`. See Components, Topology bar.
- **16-color fallback.** On a terminal without truecolor, the resolver maps: `state-failed`/`state-failed-on-raised`→ANSI Red, `state-healthy`→ANSI Green, `state-running`/`state-ambiguous`→ANSI Yellow, `accent-teal`→ANSI Cyan, `accent-navy`→ANSI Blue, `text-primary`→ANSI White/Bright White, `text-secondary`/`text-muted`→ANSI White with the `Dim` attribute (Terminal.Gui has no separate gray in the base 16), `surface-base`/`surface-raised`/`surface-overlay`→ANSI Black/Bright Black via the `Bold`/background-intensity bit. `[ASSUMPTION]` This mapping has not been rendered and screenshotted against a real 16-color terminal; it is the designer's best-effort interpretation of the nearest ANSI neighbor for each hex value.
- **Avoid:** gradients (Terminal.Gui cannot render them), a second chromatic accent beyond teal/navy, and any state communicated by hue alone.

## Typography

Terminal.Gui has no font family or size control — the host terminal emulator owns the actual glyph rendering, always monospace. "Typography" here means **attribute roles**, not font choices: `label` (bold) for headers/captions/primary-action text, `body` (normal) for the bulk of tables and forms, `meta` (dim) for timestamps/ids/footnotes, and `evidence-mono` (unstyled, never truncated or silently re-wrapped — pannable by default, with an explicit opt-in wrap toggle) for raw HTTP/JSON bodies and log lines where byte-for-byte fidelity matters more than compactness. `[ASSUMPTION]` "Projector legibility" for typography is delegated to the operator's terminal font-size setting outside this console's control; the console's own obligation is to never rely on a text size distinction to convey meaning (for example, no "small print" disclaimers) and to keep line lengths compatible with a compact/narrow terminal (see `EXPERIENCE.md` Responsive & Platform). `[ASSUMPTION]` A recommended projector baseline — legible from the back of a ~250-seat room — is 100×30 cells at roughly 18–20pt monospace; this is a starting point for dress rehearsal, not a verified claim, and is promoted to tested fact only after a real-terminal rehearsal (see `EXPERIENCE.md` Projector mode).

## Layout & Spacing

The spacing scale is in terminal cells (`ch`/`row`), not pixels: `{spacing.1}`–`{spacing.3}` for internal padding, `{spacing.gutter}` between adjacent regions, `{spacing.pane-margin}` around the outer frame, `{spacing.section-gap}` between stacked sections, `{spacing.nav-rail-width}` as the fixed compact width of the navigation rail. The persistent topology bar (top), navigation rail (left), and evidence strip (bottom) are fixed-size (their content is bounded and truncates gracefully, never wraps unpredictably); everything else — the active workspace's content down to its fixed lower action/detail region — fills the remaining space as one continuous surface, not a set of boxed sub-panels.

The Faults workspace adds three width tokens: `{spacing.fault-slider-track-width}` is the preferred bar width, `{spacing.fault-slider-track-min}` the narrowest bar still worth drawing, and `{spacing.fault-value-column}` the fixed column that holds the printed level and its staged delta. The value column is **never** sacrificed to preserve the track — the number is authoritative and the bar is reinforcement, so degradation removes the bar first and the number never (see `EXPERIENCE.md` Responsive & Platform).

**Concrete thresholds** (full behavior/rationale in `EXPERIENCE.md` Responsive & Platform): `[ASSUMPTION]` the preferred baseline is `{spacing.preferred-width}` × `{spacing.preferred-height}` (100 cols × 30 rows) — chosen as a reasonable authoring target for this brief, not yet confirmed against a real terminal/projector rehearsal (see `EXPERIENCE.md` Projector mode). Below that, the shell enters **compact** mode: the navigation rail shrinks to `{spacing.nav-rail-width-compact}` (icon+shortcut only, labels hidden) and workspace content becomes a single full-view pane instead of a side-by-side layout. `{spacing.hard-min-width}` × `{spacing.hard-min-height}` (80 cols × 24 rows) is the hard minimum; below it the console shows a non-blocking size hint but never crashes or loses state. The topology bar's resource-chip strip never hides a resource: chips abbreviate their label to a fixed 3-character code before any chip would be dropped, and a chip is never fully hidden regardless of terminal width.

## Elevation & Depth

Terminal.Gui has no shadow or blur primitive, so depth is expressed only through **tone-stepping** (`surface-base` → `surface-raised` → `surface-overlay`) and **border weight**. The console is deliberately shallow and deliberately flat: the topology bar, every workspace's content, and the lower action/detail band all sit on the same `surface-base` tone — one uninterrupted near-black work surface, separated only by hairline rules, never by a raised "panel" background. The navigation rail is the single exception, one tone up on `surface-raised`, so it reads as chrome rather than content. Exactly one layer — the confirmation modal — sits above everything on `surface-overlay` with a `surface-base` scrim implied behind it (rendered as a dimmed/unfocused background, since Terminal.Gui has no true alpha blending). No second modal ever stacks on the first; a confirmation is answered or cancelled before anything else can happen, consistent with the single-action-in-flight rule.

## Shapes

There is no corner radius in a terminal — "shape" is expressed through **box-drawing border style** instead, which is why this spine omits the `rounded` frontmatter token in favor of a `border` field on each component. Two border weights exist: **single-line hairline** for calm, reversible dividers (topology bar's bottom rule, navigation rail's right rule, evidence strip's top rule) — never a full box around workspace content — and **double-line** reserved exclusively for the confirmation modal, so a destructive action's confirmation is visually distinct from every read-only or already-confirmed surface before the operator reads a word of it. `[ASSUMPTION]` This two-weight scheme is a deliberate adaptation of the "shapes" concept to a non-CSS platform, not a literal instruction from any source document.

## Components

Every component below is paired with a same-named **Component Patterns** row in `EXPERIENCE.md` (behavioral rules there; visual tokens here) — see that file for the behavior each visual spec supports.

- **Topology bar** (`{components.topology-bar}`) — persistent, one row, top of shell, on `surface-base` like the canvas beneath it. Shows the current AppHost profile (Regular/LoadTests), Owned/Attached badge, the persistent fault chip (`{components.status-chip-fault}`), and a horizontal strip of resource status chips. Never scrolls out of view. Refresh cadence, hysteresis, and immediate command-state behavior are defined in `EXPERIENCE.md` Component Patterns.
- **Navigation rail** (`{components.nav-rail}`) — persistent, compact, fixed-width (`{spacing.nav-rail-width}`) column on the left edge of the shell, `surface-raised` with a single hairline-right divider. Hosts one **nav rail item** (`{components.nav-rail-item}`) per workspace — `accent-navy` fill when active, `surface-raised` when inactive — each reachable by a single keypress (`1`–`5`) or click; never nested, never a horizontal tab strip. Below `{spacing.preferred-width}`, shrinks to `{spacing.nav-rail-width-compact}` (icon+shortcut only, labels hidden).
- **Status chip** (`{components.status-chip-healthy}` / `{components.status-chip-running}` / `{components.status-chip-unknown}` / `{components.status-chip-failed}`) — symbol + short label + color, per the **Colors** rule that color is never the sole channel. Used in the topology bar and, per **Activity row** below, in the left-hand status gutter of every activity/resource row. Each chip is an individually focusable/Tab-reachable control (see `EXPERIENCE.md` Accessibility Floor for the literal focus order); Enter/click on a resource chip opens that resource's detail in the Resources workspace.
- **Activity row** (`{components.activity-row}`) — the atomic unit of every workspace's main content (resource rows, payment/evidence entries, invariant chips): a bold verb/object headline (`label` typography, `foreground-headline`) — for example, "Stop — corebank-api" — with a muted command/request detail line directly beneath it (`meta` typography, `foreground-meta`) — for example, the exact Aspire command or HTTP call issued — and a fixed-width status marker rendered in a left gutter (`gutter-width`) column, never inline-mixed with the headline text. Sits directly on `surface-base`, borderless, so a scrolling list of rows reads as one continuous surface rather than stacked cards. An empty workspace renders a single muted (`text-muted`) placeholder row in the same slot — for example, "No payments submitted this session" — never a blank void.
- **Resource row** (`{components.resource-row}`) — an **Activity row** specialization: fixed `action-slot-width` at the row's right edge always shows the single legal next command (Start/Stop/Restart) for that resource in the exact same column position regardless of which label is showing, so the recovery action lands exactly where the prior action was. During a commanded transition, the row's status marker shows `status-chip-running`/`~` plus an elapsed-time readout in the `meta` line — for example, "Restarting — 4s."
- **AppHost control panel** (`{components.apphost-control-panel}`) — `surface-base`, borderless, one row per AppHost profile (Regular/LoadTests). Each row shows the profile name, an Owned (`state-healthy`-tinted badge) or Attached (`state-neutral`-tinted badge) indicator, and up to four action slots — Start/Attach use `primary-action-button` tokens, Stop/Switch use `destructive-action-button` tokens. When the current topology is Attached, the whole-AppHost Stop and Switch slots render in their `destructive-action-button` position but visibly disabled (dimmed to `text-muted`, no focus ring) — never hidden — while per-resource Start/Stop/Restart on that same Attached topology remain live, styled identically to a normal **Resource row**'s action slot, so "this whole-AppHost control is off" and "this one resource control is on" read as two distinct, deliberate visual states rather than one blanket-disabled panel.
- **Payment form** (`{components.payment-form}`) — `surface-base`, borderless, `label`-typography field captions (`foreground-label`) beside `body`-typography editable values (`foreground-value`); the `standard`/`instant` rail is two adjacent chips (`rail-chip-selected`/`rail-chip-unselected`, same visual language as the **Idempotency selector**). Submit renders as this workspace's one `primary-action-button`, anchored in the fixed lower action/detail region.
- **Idempotency selector** (`{components.idempotency-selector}`) — a three-chip segmented control (Generated / Supplied / Omitted) using `chip-selected-*`/`chip-unselected-*` tokens identical in kind to the payment form's rail chips; the Omitted chip additionally renders a `warning-foreground` (`state-ambiguous`) inline caption — "not retry-safe after an ambiguous outcome" — beneath it at all times, not only after submission.
- **Resend-same-key action** (`{components.resend-same-key-action}`) — when enabled, identical token spec to `primary-action-button`; when disabled (after an Omitted-key submission resolves ambiguously), renders as `disabled-foreground` (`text-muted`) label text with no fill and no focus ring, in the same screen slot, so its disablement is legible rather than merely non-responsive.
- **Burst control** (`{components.burst-control}`) — `surface-base`, a bounded numeric field (`body` typography) plus three live `meta`-typography counters (accepted/completed/failed) each prefixed with the matching status-chip symbol/color (`counter-accepted`/`counter-completed` reuse `state-healthy`, `counter-failed` reuses `state-failed`). Its **Cancel** control is one of the console's named exemptions from the global single-action-in-flight lock (see `EXPERIENCE.md` State Patterns): it renders with a single `accent-teal` outline border and no fill (`cancel-fill: none`) — visually distinct from both a normal enabled `primary-action-button` (filled) and a normal disabled control (`text-muted`, no border) — so a presenter can see at a glance that Cancel is still live while every other mutating control is dimmed. This outline-teal-no-fill treatment is now a **shared signature**, not a one-off: see Lock-exempt control signature below.
- **Outcome query** (`{components.outcome-query}`) — `surface-base`, `text-primary` foreground, `always-enabled: true`: unlike every other mutating control, it never dims or disables during the single-action-in-flight lock, rendering identically whether or not another action is in flight — a visual promise, not just a behavioral one, that read-only lookup is always available.
- **Lock-exempt control signature** — a cross-cutting rule rather than a component: every control that stays live while the global single-action-in-flight lock is held renders as a single `accent-teal` outline with **no fill** and never dims. Today that is exactly three controls — the burst control's Cancel, every fault slider, and panic-off. The signature exists so that when the console dims for an in-flight action, the handful of controls that remain usable read as one recognizable family at a glance, instead of each looking like an unexplained local inconsistency. No control may adopt this treatment without being genuinely lock-exempt, and no lock-exempt control may omit it.
- **Fault arming toggle** (`{components.fault-arming-toggle}`) — a two-state pill in the Resources workspace, replacing the former DevProxy on/off toggle: `armed-background`/`armed-foreground` (filled `accent-teal`/`surface-base`) when the next AppHost start will bring up a Dev Proxy, `unarmed-background`/`unarmed-foreground` (`surface-raised`/`text-secondary`) when it will not, and `unavailable-foreground` (`text-muted`, no fill) on an Attached topology this session cannot re-arm. Its caption always names the launch-time truth ("faults armed on next start"), never a bare "on" — the visual weight of a filled teal pill would otherwise read as "faults are happening now," which it does not mean.
- **Fault slider** (`{components.fault-slider}`) — one horizontal row per knob on `surface-base`, borderless: a `label`-typography caption on the left, a `track-width` bar, and a fixed `value-column` to the right of the track that always renders the exact numeric value in `body` typography. The track is drawn in `track` (`border-hairline`); the portion representing the **currently live** level fills in `fill-live` (`accent-teal`, the lock-exempt signature), and any **staged** level not yet applied fills in `fill-staged` (`text-secondary`) with both numbers shown in the value column as an explicit delta (`5% → 40%`). The `handle` is `text-primary`, reverse-video on focus. The latency knob carries two handles (floor and ceiling) on one track. Disabled (unarmed/Attached topology) renders label, track, and value in `disabled-foreground` — the value stays legible, because the operator must still be able to read what *would* be applied. **No token encodes severity**: the bar's fill length and the printed number are the only severity channels (see Colors).
- **Fault preset chips** (`{components.fault-preset-chip}`) — a segmented chip row using the same `chip-selected-*`/`chip-unselected-*` language as the idempotency selector and the payment form's rail chips, so "pick one of a small fixed set" looks identical everywhere in the console. When any knob is moved off its preset, the row's selection clears and a `custom-label-foreground` (`text-secondary`) caption reads `Custom` — the console never leaves a preset chip looking selected while the values no longer match it.
- **Apply faults action** (`{components.apply-faults-button}`) — the Faults workspace's one `primary-action-button`, anchored in the same fixed lower action/detail region as every other workspace's primary action. It is **not** given `destructive-action-button` tokens: faults destroy no state, are fully reversible, and are undone by one key, and dressing them in the double-bordered red treatment would blunt that treatment where it actually guards data loss. With nothing staged it renders `disabled-foreground`/`disabled-fill: none` in the same slot; while staged, `staged-delta-foreground` (`text-secondary`) prints the count of knobs about to change in its caption.
- **Panic-off control** (`{components.panic-off-control}`) — single `accent-teal` outline, no fill, `always-enabled`, carrying the lock-exempt signature it shares with burst Cancel and the sliders. Bound to `0` from every workspace. It never renders as a `destructive-action-button` and never opens the confirmation modal: it is the console's one control whose entire purpose is to make the running system safer, and gating it would invert the meaning of every other gate in this file.
- **Replica indicator** (`{components.replica-indicator}`) — a bare `text-secondary` numeral, for example "×3"; the `stepper-border`/`stepper-foreground` (single border, `accent-teal`) increment/decrement control renders only when the current Aspire command surface actually supports scaling that resource — otherwise the numeral appears alone, with no button token at all, never a disabled-looking button that implies scaling should work.
- **Load phase strip** (`{components.load-phase-strip}`) — five fixed cells in a single horizontal row, divided by single-hairline verticals, in the fixed sequence `Reset → Run → Wait → Assert → Investigate` (Reset is the workflow's first internal phase, never a separate/standalone control — see `EXPERIENCE.md` Component Patterns, Load phase strip). Each cell shows `cell-not-yet-reached` (`state-neutral`) before it starts, `cell-active` (`state-running`, `~`, plus an elapsed-time readout) while running, and resolves to `cell-passed` (`state-healthy`) or `cell-failed` (`state-failed`). Before any run this session, all five cells show `cell-not-yet-reached`.
- **Invariant chip** (`{components.invariant-chip}`) — one chip per invariant (exactly-once, no-loss, balance-conserved, ordering, drained) plus a distinct inline-instant-settlement chip: single-`border` (unlike the borderless resource status chips, so the two families are never visually confused), `foreground-pass`/`foreground-fail`/`foreground-not-yet-observed` (`state-healthy`/`state-failed`/`state-neutral`) each shown individually, never collapsed into one pass/fail bit. Before any run this session, all chips show `foreground-not-yet-observed`.
- **Primary action button** (`{components.primary-action-button}`) — `accent-teal` fill, single border, reverse-video on focus. Exactly one exists per screen at a time, anchored in the workspace's fixed lower action/detail region (see `EXPERIENCE.md` Information Architecture) directly above the evidence strip, so the operator's hand/eye never has to relocate it mid-sentence. When a workspace's primary action is itself destructive (the Load Test workspace's **Run**, which resets the disposable topology as its first phase — see Load phase strip), it instead renders with `destructive-action-button` tokens, not `primary-action-button` tokens, and is gated by the same **Confirmation modal**.
- **Destructive action button** (`{components.destructive-action-button}`) — `surface-raised` fill with `state-failed-on-raised` text (a lightened "on-raised" tint, not the base `state-failed` hex, chosen because `state-failed` itself measures a sub-AA 4.12:1 on `surface-raised` while `state-failed-on-raised` measures 4.91:1 — see Colors; never a solid red fill either way, so a stray glance must not read "already failed"), double border, gated by the **Confirmation modal**'s single typed confirmation key (`Y`) — no hold gesture, no double-Enter. Its resolved action, such as "Restart," always renders in the exact screen position the destructive action ("Stop") occupied. Applies to resource Stop/Restart, AppHost Stop/Switch, and Load Test Run.
- **Evidence strip** (`{components.evidence-strip}`) — persistent, one row, bottom of shell, hairline-top border separating it from the primary action button immediately above it. Together, the primary action button and the evidence strip form the workspace's fixed lower action/detail region — both `surface-base`, divided only by a hairline, never a boxed sub-panel. Shows the single most recent action's truthful one-line outcome, labeled with the topology/profile it was captured against. Never itself scrolls; "Details" opens the full record in the Evidence/Results workspace. Before any action this session, shows a muted (`text-secondary`) placeholder — "No actions yet this session."
- **Confirmation modal** (`{components.confirmation-modal}`) — `surface-overlay`, double border, appears only for a disruptive resource/topology/Load-Test-Run command, always names the exact target and exact command before it can be confirmed. Opens with focus on **Cancel** (`focus-on-open: cancel`, the safer default), traps focus entirely — nothing else on screen is reachable while it is open — and requires the single explicit confirmation key `Y` (`confirm-key`); `Escape` (`cancel-key`) cancels. On either confirm or cancel, focus returns to the exact control that opened it. No hold-to-confirm gesture and no double-Enter confirmation exist anywhere in this console.

## Do's and Don'ts

| Do | Don't |
|---|---|
| Pair every status chip with a symbol and a text label | Communicate state through color alone |
| Reserve `state-failed` (red) for a proven failure | Use red for "in progress," "unknown," or "ambiguous" |
| Reserve `state-healthy` (green) for a proven pass/healthy resource | Use green optimistically before evidence exists |
| Keep the primary action in one fixed screen position per workspace | Relocate the primary action based on content length |
| Use `accent-teal` only for the primary action and focus ring | Use `accent-teal` for decoration or a second competing accent |
| Render raw evidence (`evidence-mono`) unstyled and byte-faithful, pannable, wrap-toggle optional | Reformat, summarize, or truncate a raw response body silently |
| Keep the confirmation modal one layer deep, double-bordered, opened with Cancel focused | Stack a second modal, or open a confirmation with the destructive action pre-focused |
| Gate every destructive action with the single typed confirmation key (`Y`) | Use a hold-to-confirm gesture or accept double-Enter as confirmation |
| Let the terminal emulator's font size own legibility | Simulate "small print" or a text-size-based meaning distinction |
| Keep the work surface on one continuous `surface-base` tone with hairline dividers | Box workspace content in a raised "panel" background |
| Keep workspace navigation in the compact, persistent left rail with one-key shortcuts | Reintroduce a horizontal tab strip or nest navigation |
| Show the fault chip persistently in the topology bar across every workspace | Let fault injection stay silently in force after leaving the Faults workspace |
| Carry severity in the printed number, the bar fill length, and the label | Encode fault severity in color, or add a green-amber-red severity ramp |
| Reserve `accent-teal` for its three stated roles — primary action, focus ring, lock-exempt/faults-in-force | Let a fourth teal role appear, or give a pass/fail state the teal treatment |
| Give every lock-exempt control the same outline-teal-no-fill signature | Style a control as lock-exempt when it is not, or dim one that is |
| Render a staged level and its live level together as an explicit delta | Show a staged number alone, where it can be misread as the current level |
| Label the arming toggle with its launch-time meaning | Let a filled teal arming pill imply faults are being injected right now |
| Keep Apply on `primary-action-button` tokens and panic-off always enabled | Dress a reversible fault change in the destructive red/double-border treatment |
