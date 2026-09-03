---
title: 'DemoRunner UX spines — terminal accessibility & projector legibility review'
type: 'ux-review'
reviewed:
  - DESIGN.md
  - EXPERIENCE.md
  - .memlog.md
  - imports/operator-console-inspiration.png
  - ../../briefs/brief-demorunner-console-2026-09-03/brief.md
  - ../../../implementation-artifacts/spec-7-4-reusable-terminal-demo-operator-console.md
  - ../../../../adr/ADR-015-presentation-safe-terminal-demo-console.md
created: '2026-09-03'
---

# Review — Terminal accessibility & projector legibility (re-validation)

Scope: `DESIGN.md` + `EXPERIENCE.md` behavioral/visual contracts only. No other file was modified for this review. This is a fresh pass against the corrected spine pair — every prior finding was re-checked by recomputation or re-extraction, not by re-reading the prose alone.

## Overall verdict

**Pass.** The critical defect from the prior pass — `text-muted` numerically failing its own stated WCAG AA floor — is fixed and independently re-verified: recomputing contrast from the current hex values (`#7C8CA0` on `#0B1220`/`#132036`) gives 5.45:1 / 4.75:1, matching the spine's own stated numbers and clearing 4.5:1 on both backgrounds. The destructive-button contrast gap is also fixed via a dedicated `state-failed-on-raised` tint (recomputed at 4.91:1, vs. the base `state-failed`'s 4.12:1 on the same background, which the spine now correctly avoids for that one pairing). Every other High/Medium finding — focus order, hold-to-confirm ambiguity, the `◐` glyph, compact-terminal thresholds, chip overflow, evidence panning, refresh/flap debounce, confirmation-modal focus containment — is resolved with a concrete, checkable rule in the current text. The composition-reference attribution was corrected in the final pass.

## Independently recomputed contrast ratios

Recomputed all load-bearing pairs using the WCAG 2.x relative-luminance formula directly from the hex values in `DESIGN.md`'s frontmatter (not copied from the prose):

| Foreground | Background | Computed | Spine's stated value | Match |
|---|---|---|---|---|
| `text-primary` `#E8ECF1` | `surface-base` `#0B1220` | 15.78:1 | 15.78:1 | ✓ |
| `text-primary` | `surface-raised` `#132036` | 13.75:1 | 13.75:1 | ✓ |
| `text-secondary` `#9AA7B8` | `surface-base` | 7.66:1 | 7.66:1 | ✓ |
| `text-secondary` | `surface-raised` | 6.67:1 | 6.67:1 | ✓ |
| `text-muted` `#7C8CA0` | `surface-base` | 5.45:1 | 5.45:1 | ✓ (clears 4.5:1) |
| `text-muted` | `surface-raised` | 4.75:1 | 4.75:1 | ✓ (clears 4.5:1) |
| `surface-base` (label) | `accent-teal` `#2FB7A8` | 7.54:1 | 7.54:1 | ✓ |
| `text-primary` | `accent-navy` `#325F8C` | 5.62:1 | 5.62:1 | ✓ |
| `state-failed` `#D9534F` | `surface-base` | 4.73:1 | 4.73:1 | ✓ |
| `state-failed` | `surface-raised` | 4.12:1 (sub-AA) | 4.12:1 (spine names this as the reason it is not used here) | ✓ |
| `state-failed-on-raised` `#E06862` | `surface-raised` | 4.91:1 | 4.91:1 | ✓ (clears 4.5:1) |
| `state-healthy` `#3FBF63` | `surface-base` | 7.88:1 | 7.88:1 | ✓ |
| `state-running`/`state-ambiguous` `#D8A93B` | `surface-base` | 8.61:1 | 8.61:1 | ✓ |

Every declared ratio in `DESIGN.md` matches the independently recomputed value exactly — the spine's contrast claims are numerically trustworthy, not just internally consistent.

## Re-verification of prior findings

1. **Was Critical — `text-muted` failed its own stated contrast floor.** Resolved and recomputed above. The hex was deliberately raised from `#5B6B80` (3.44:1/3.00:1, failing) to `#7C8CA0` (5.45:1/4.75:1, passing), and `DESIGN.md:190` states the before/after numbers and the reason, so the fix is traceable rather than silent.
2. **Was High — Destructive-button label contrast sub-AA.** Resolved via a dedicated `state-failed-on-raised` tint (4.91:1) used *only* on the one background where the base red fails (4.12:1 on `surface-raised`); `DESIGN.md:242` states both numbers and why the split exists.
3. **Was High — Focus order conflated reading order with actual Tab order.** Resolved. `EXPERIENCE.md:131` now enumerates the literal per-workspace Tab sequence (topology bar: chip 1…N, then DevProxy chip, then quick-open 1…M → nav rail: item 1…4 → workspace content: row 1…N then primary/contextual action → evidence strip: Details), states `Shift+Tab` reverses it exactly, and explicitly states there is no wrap at either end. The confirmation modal's self-contained trapped-focus behavior is called out as the one exception to shell-wide Tab order.
4. **Was High — "Hold-to-confirm" had no defined keyboard mechanism.** Resolved. Hold-to-confirm and double-Enter are now explicitly banned everywhere (`EXPERIENCE.md:119`, `DESIGN.md:242,257`); the sole mechanism is a single typed confirmation key (`Y`), with the rationale stated (auto-repeat is OS/terminal-dependent and not a reliable substitute; Terminal.Gui has no "held for N ms" primitive).
5. **Was High — `◐` symbol hardest to distinguish at distance, no fallback.** Resolved. The running/ambiguous symbol changed to the ASCII `~` (`DESIGN.md:199`), with the rationale stated (inconsistent glyph rendering across monospace fonts, hardest of the four to read at projector distance) and an explicit ASCII-only bracket fallback (`[#]`/`[~]`/`[ ]`/`[X]`) defined for terminals that can't render any of the four symbols. The text label is stated as the primary discriminator for this specific state, with the symbol as secondary reinforcement.
6. **Was Medium — No numeric compact-terminal threshold.** Resolved. `DESIGN.md:212` states concrete values: preferred baseline 100×30 cells, compact mode below that, hard minimum 80×24 — matching the same numbers in `EXPERIENCE.md` Responsive & Platform.
7. **Was Medium — Topology-bar chip overflow undefined.** Resolved. Both files state the same rule: chip labels abbreviate to a fixed 3-character code before any chip would be dropped, and a chip is never fully hidden regardless of width (`DESIGN.md:212`, `EXPERIENCE.md:144`).
8. **Was Medium — Long raw evidence had no horizontal-navigation contract.** Resolved. The `evidence-mono` typography note and the Responsive & Platform "Long/unwrapped evidence" row both state the same pan/wrap-toggle contract: arrow keys / `Shift+←/→` pan the unwrapped default view with a "more →" indicator; an explicit opt-in wrap toggle re-flows the same bytes for reading without altering them.
9. **Was Medium — No refresh cadence or flap-debounce for continuously updating state.** Resolved. `DESIGN.md:226` states an explicit polling interval (at most once every 1–2 seconds) and a hysteresis rule (a resource must hold a new state for one full poll interval before its chip changes), with an explicit carve-out for operator-commanded transitions, which flip immediately rather than waiting for the next poll.
10. **Was Low — No minimum-size/font guidance for projector use.** Resolved as a properly-scoped pending-verification assumption, not a gap: `DESIGN.md` Typography and `EXPERIENCE.md` Responsive & Platform (Projector mode row) both state a recommended starting baseline (100×30 cells, ~18–20pt monospace, back of a ~250-seat room) and explicitly label it `[ASSUMPTION]`, to be promoted to a tested fact after a real dress rehearsal. This is exactly the right way to carry an open risk forward, and per this task's standing guidance, a clearly-labeled, non-contradictory `[ASSUMPTION]` is not itself a defect.
11. **Was Low — Confirmation-modal focus containment asserted, not specified.** Resolved. Both files now state where focus lands on open (`Cancel`, the safer default) and that it returns to the exact control that opened the modal on either outcome (`DESIGN.md:244`, `EXPERIENCE.md:131`).

## Note on the imported reference image

`imports/operator-console-inspiration.png` was viewed directly for this pass. It is a terminal-session transcript screenshot (top tab strip: "Current / Sessions / Issues / Pull requests / Gists"; a scrolling command/response log; a fixed bottom input-and-action region) — not a Docker Sandboxes-style UI. The prior review's citation-mismatch finding (the file was previously mislabeled as a "Docker Sandboxes" screenshot) is resolved: both spines and `.memlog.md` now correctly describe it as a generic, user-supplied composition reference, and no longer attribute its appearance to Docker Sandboxes. A more specific residual issue — one of the "lifted" composition principles doesn't actually match what this image shows — is a provenance/traceability question best owned by the rubric walker's Visual reference coverage category (see `review-rubric.md` § 5) and is not re-counted here to avoid double-scoring the same underlying fact.

## What is already strong

- **Color is never the sole channel** — every state pairs a fixed symbol and label, stated as a hard rule in both files.
- **Honest non-claim on screen readers** — `EXPERIENCE.md` still explicitly scopes accessibility to keyboard operability and color-independent state, not screen-reader support, rather than an untested claim.
- **Latency/motion honesty now covers the whole console**, not just the Load Test Wait phase — every resource/AppHost transition carries the same elapsed-time-readout treatment.
- **Destructive-action recoverability remains a location guarantee** — the inverse action always renders in the exact slot the original occupied.
- **16-color fallback is stated in prose** with an explicit per-token ANSI mapping, honestly flagged as unverified against a real terminal rather than presented as tested fact.
- **Single-action-in-flight and debounced double-activation** remain in place, now with the Burst-Cancel exception explicitly named rather than left as an unstated gap.
