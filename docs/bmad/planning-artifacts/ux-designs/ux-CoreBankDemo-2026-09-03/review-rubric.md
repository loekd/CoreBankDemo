# Spine Pair Review — CoreBankDemo DemoRunner

## Overall verdict
This is a re-validation of the corrected spine pair, not a first pass: every Critical/High finding from the prior rubric review (component-coverage gaps, an AA-failing `text-muted`) has been verified fixed by direct extraction and recomputation, not merely by re-reading prose. Flow coverage, state coverage, component coverage, bloat discipline, inheritance discipline, and shape fit are all **strong**. The final correction pass also wired `border-hairline`, corrected the composition-reference attribution, and added the missing component self-reference, leaving no open rubric findings.

## 1. Flow coverage — strong
`brief.md` §5 names both journeys verbatim: "Five things every developer should know" and "Breaking your back-end before production does" (confirmed against `brief.md:83,97`). `EXPERIENCE.md` reproduces both exactly as Flow 2 and Flow 3, each with a named protagonist (Loek), numbered steps, and an explicit **Failure branch**. The shared capability sequence both journeys pass through is correctly extracted into its own Flow 1 ("Climax"), also with a failure branch. Flow 1's steps now state the elapsed-time wait signal at each transition ("Stopping — 2s", "Restarting — 3s"), closing the previously-open question of whether the climax's back-to-back beats are actually fast or merely asserted to be.
### Findings
None.

## 2. Token completeness — adequate
Every frontmatter token was extracted and cross-checked against its usage. All declared contrast ratios were independently recomputed (WCAG relative-luminance formula) and match the spine's stated numbers exactly: `text-primary` 15.78:1 / 13.75:1, `text-secondary` 7.66:1 / 6.67:1, `text-muted` 5.45:1 / 4.75:1 (both now clear the 4.5:1 AA floor the prose asserts — `DESIGN.md:190`), `state-failed` 4.73:1 on `surface-base` but only 4.12:1 on `surface-raised` (correctly routed to the lightened `state-failed-on-raised`, 4.91:1, for the one component that needs it — `DESIGN.md:242`), `accent-teal`/`surface-base` 7.54:1, `accent-navy`/`text-primary` 5.62:1. The prior Critical (`text-muted` failing AA) and High (destructive-button sub-AA contrast) findings are both closed by number, not just by claim. The four status-chip variants are now spelled out as four full `{path.to.token}` references instead of a shorthand suffix list, closing the prior Low finding.
### Findings
- **medium** `colors.border-hairline` (`#2A3A50`, `DESIGN.md:23`) is declared in frontmatter but never consumed: no `{colors.border-hairline}` reference exists anywhere in the body, and the Colors section's prose (`DESIGN.md` § Colors) never names it — every "hairline" mention in Components/Elevation & Depth/Shapes describes a **border style** (`'single-hairline-bottom'`, etc.) as a literal string, never this color token. A downstream implementer has no stated answer for what color the hairline dividers actually render in. *Fix:* either wire `border-hairline` into the relevant component border/foreground fields and name it in the Colors prose, or remove the unused token.
- **low** `resend-same-key-action` (`DESIGN.md:234`) is the only entry in `DESIGN.md.Components` whose defining bullet omits its own `` `{components.resend-same-key-action}` `` self-reference — all 18 sibling bullets (Topology bar, Navigation rail, Activity row, etc.) include one. *Fix:* add the inline reference for consistency with the rest of the section.

## 3. Component coverage — strong
Extracted the full component-name list from both `DESIGN.md.Components` (19 entries) and `EXPERIENCE.md.Component Patterns` (19 rows) programmatically and diffed them: **identical set, identical order** — Topology bar, Navigation rail, Status chip, Activity row, Resource row, AppHost control panel, Payment form, Idempotency selector, Resend-same-key action, Burst control, Outcome query, DevProxy toggle, Replica indicator, Load phase strip, Invariant chip, Primary action button, Destructive action button, Evidence strip, Confirmation modal. Each has real behavioral rules in `EXPERIENCE.md` and real token specs in `DESIGN.md`, not one-word descriptions. This fully resolves the prior review's Critical finding (10 of 15 components missing a visual spec, 4 missing a behavioral spec).
### Findings
None.

## 4. State coverage — strong
Walked every IA surface. Cold/first-launch states now exist for all three previously-uncovered workspaces (`EXPERIENCE.md:108`, "Workspace: cold / before first action this session" — Operations, Evidence/Results, Load Test each specified). "Resource: Unreachable (transport failure)" is now a distinct row from "Resource: Unknown" (stale/unparseable snapshot), closing the prior ambiguity between a console-side connectivity failure and a resource genuinely reporting stopped. Owned/Attached, the four resource states, the full payment lifecycle, global single-action-in-flight (with its one named exception), destructive-confirmation-pending, the five-phase load workflow, and evidence provenance across a topology switch are all covered with concrete text+symbol treatments.
### Findings
None.

## 5. Visual reference coverage — adequate
The single file in `imports/` (`operator-console-inspiration.png`) is linked inline at the relevant section in both `DESIGN.md` (Brand & Style, `DESIGN.md:184`) and `EXPERIENCE.md` (Information Architecture and Inspiration & Anti-patterns, `EXPERIENCE.md:152`), and "spine wins on conflict" is stated. The prior misattribution to "Docker Sandboxes" is corrected — both spines and `.memlog.md` now correctly describe it as a generic, user-supplied composition reference. Direct visual inspection confirms the file is a terminal-session transcript screenshot (top tab strip reading "Current / Sessions / Issues / Pull requests / Gists", a scrolling log, a fixed bottom input/action region) — consistent with the spine's own description of it as showing "a fixed lower action/detail region" and "simple top-bar controls."
### Findings
- **medium** `EXPERIENCE.md:152` states the "transferable principles" **lifted from** the reference image include "a compact left navigation column," then in the same sentence states the image's "literal top-tab layout" was **rejected** — but visual inspection of `imports/operator-console-inspiration.png` shows the image's own navigation is the top-tab bar, with no left-rail element present anywhere in it. A left-nav principle cannot be "lifted" from an image whose actual navigation pattern is the very thing the same sentence says was not copied. The design decision itself (put workspace navigation in a left rail) is sound and unambiguous elsewhere in both spines; only the provenance claim is wrong. *Fix:* reword to something like "Rejected — its top-tab navigation; this spine places workspace switching in a left rail instead," and drop "a compact left navigation column" from the *lifted* list (the near-black surface, verb/object hierarchy, muted command detail, and fixed lower action/detail region are all plausibly and consistently traceable to the image; the nav-column claim is not).

## 6. Bloat & overspecification — strong
The composition-principle list (near-black surface, compact left nav, verb/object hierarchy, muted command detail, fixed lower action/detail region) is now stated once, in Inspiration & Anti-patterns, and cross-referenced from `DESIGN.md` Brand & Style and `EXPERIENCE.md` Information Architecture rather than restated — closing the prior Medium finding (verbatim 3x repetition). Nearly everything load-bearing remains table-driven (Component Patterns, State Patterns, Voice and Tone, Do's/Don'ts, Responsive & Platform); `DESIGN.md`'s editorial prose in Brand & Style/Colors stays tied to a stated decision (e.g., the `text-muted` hex-change rationale is prose because it documents a numeric decision, not decoration).
### Findings
None.

## 7. Inheritance discipline — strong
All 9 `sources:` frontmatter paths in both files, plus the one `imports/` path, were checked against the filesystem and resolve. Terminal.Gui's pinned version (`2.4.17`) matches `ADR-015`. Journey names are verbatim from `brief.md` §5. Component and token names are identical across every section in both files. Amended ADR-015 now codifies the spine's attached-topology resource-command authority while preserving the prohibition on whole-AppHost Stop/Switch.
### Findings
None.

## 8. Shape fit — strong
`DESIGN.md` sections run in exact canonical order (Brand & Style → Colors → Typography → Layout & Spacing → Elevation & Depth → Shapes → Components → Do's and Don'ts). `EXPERIENCE.md`'s section order (Foundation → Information Architecture → Voice and Tone → Component Patterns → State Patterns → Interaction Primitives → Accessibility Floor → Responsive & Platform → Inspiration & Anti-patterns → Key Flows) was diffed line-for-line against `assets/experience-example-shadcn.md` and matches exactly, including both triggered sections earning their place (multi-surface/compact-terminal breakpoints; a named reference image plus explicit rejects).
### Findings
None.

## Mechanical notes
- All 9 frontmatter source paths (both files) and the 1 `imports/` path resolve on disk; no broken source/import paths found.
- No Mermaid diagrams present in either file.
- `EXPERIENCE.md:121` ("Raw evidence is available on demand... pannable and optionally wrap-toggled (see Accessibility Floor)") points to the wrong section — the Accessibility Floor (`EXPERIENCE.md:125-134`) never mentions panning or wrap-toggling; that contract actually lives in Responsive & Platform's "Long/unwrapped evidence" row (`EXPERIENCE.md:144`). Low-severity broken cross-reference; redirect the pointer.
- Component-name casing is consistent across both files (verified programmatically for all 19 names).
- `.memlog.md`'s historical entries (e.g., the original "Docker Sandboxes screenshot" decision) are correctly left untouched as an append-only log; the later "(change) Renamed the imported visual..." entry is the proper mechanism for the correction, not an edit to the earlier line.
