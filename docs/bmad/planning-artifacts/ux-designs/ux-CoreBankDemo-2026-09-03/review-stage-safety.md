---
title: 'Adversarial review: DemoRunner UX spines — live-stage safety and recoverability'
type: 'review'
created: '2026-09-03'
reviewed:
  - DESIGN.md
  - EXPERIENCE.md
  - .memlog.md
  - imports/operator-console-inspiration.png
  - ../../briefs/brief-demorunner-console-2026-09-03/brief.md
  - ../../epics.md (Story 7.4 acceptance criteria)
  - ../../../implementation-artifacts/spec-7-4-reusable-terminal-demo-operator-console.md
  - ../../../../adr/ADR-015-presentation-safe-terminal-demo-console.md
  - ../../../../adr/ADR-018-instant-payment-rail.md
---

# Adversarial review: live-stage safety and recoverability (re-validation)

Lens: a presenter operating alone under time pressure while talking, in front of an audience, for a 55-minute session with no retry on visible failure. Scope is contract contradictions and missing states in `DESIGN.md`/`EXPERIENCE.md`, not visual style. This is a fresh pass against the corrected spine pair, verifying each prior finding by re-reading the current text rather than assuming the fix landed.

## Overall verdict

**Pass.** All six high-risk moments and all ten findings from the prior pass were re-checked against the current `DESIGN.md`/`EXPERIENCE.md` and are resolved by number, not by rewording alone: the recovery-on-relaunch contract is fully specified and internally consistent, burst cancellation is explicitly named as the sole exception to the global mutating-action lock, Reset has a stated singular location (Run's first internal phase), every resource/AppHost transition carries an elapsed-time "still working" signal, and DevProxy state is mirrored persistently in the topology bar. Amended ADR-015 now codifies the selected Attached-topology resource-command behavior.

## Re-verification of prior findings

1. **Was Critical — "Return to a known state" after an interruption undefined.** Now resolved. `EXPERIENCE.md:23` ("Recovery on relaunch") states explicitly: the console rehydrates *only* by re-reading live Aspire/HTTP state on every launch, including a crash or intentional close mid-outage; operation/payment history always starts empty; nothing is ever auto-restored from a prior run's local state, journal, or checkpoint; there is no "resume" and no "last known good" claim anywhere. The orphaned "the brief allows... resume-from-checkpoint" language that previously misattributed a source is gone — confirmed absent by search (`checkpoint`/`journal`/`resume` mentions in the current files only appear inside the explicit ban list and the retired-cue-player retrospective, `EXPERIENCE.md:123,153`). A presenter whose console crashes mid-outage now has a stated, falsifiable answer: relaunch, re-read live state, start with empty history.
2. **Was Critical — Burst cancellation not exempted from the global single-action-in-flight lock.** Now resolved. `EXPERIENCE.md:104` ("Single action in flight") explicitly names two exception categories, the second being "the in-flight action's own Cancel control where one exists (currently only the Burst control's Cancel) — it is the one control exempt from the lock it itself holds, so a running burst can always be stopped." `DESIGN.md`'s `burst-control` token block gives Cancel a visually distinct token set (outline border, no fill) so the exemption is legible, not just behaviorally true.
3. **Was High — Attached-topology resource-level restart contradicted ADR-015's attach boundary.** Resolved. Amended ADR-015 separates whole-AppHost ownership from resource-command authority: an attached AppHost cannot be stopped or switched, while a fresh fingerprint match permits individually confirmed, allow-listed resource Start/Stop/Restart commands.
4. **Was High — Reset declared to exist but had no IA location.** Now resolved. Reset is consistently described everywhere as Run's own first internal phase, never a standalone control: the Load Test workspace's capability list (`EXPERIENCE.md:45`), the Load phase strip component row (`EXPERIENCE.md:85`), the Load-workflow state-pattern row (`EXPERIENCE.md:106`), and Interaction Primitives (`EXPERIENCE.md:122`) all say the same thing — every rerun (including Journey B's repeated reruns across planted-bug branches) resets the disposable topology again through the same accepted sequence. No ambiguity remains about whether a second Run silently resets first.
5. **Was Medium — No "still working, not hung" affordance for Resource stop/restart, AppHost start, or Switch.** Now resolved. `EXPERIENCE.md:100` extends the Load workflow's latency-honesty pattern explicitly to Resources/AppHost: "the moment the operator commands a transition, that row/panel shows `state-running`/`~` immediately... plus an elapsed-time readout (e.g. 'Restarting — 4s')... so the climax's Stop→submit-payments→Restart beats have a stated, falsifiable wait signal at each transition."
6. **Was Medium — Flow 1 (the climax) narrated a fluid sequence the rules didn't guarantee was fast.** Now resolved. Flow 1's steps 1 and 4 explicitly narrate the wait signal in place ("Stopping — 2s", "Restarting — 3s") and state that Submit/other controls only re-enable once Aspire confirms the transition resolved — the speed claim is now falsifiable against the same interaction rule that gates it, rather than asserted as smooth without support.
7. **Was Medium — DevProxy state had no persistent visibility outside Resources.** Now resolved. The topology bar's persistent DevProxy chip (`{components.status-chip-devproxy}`) is stated to be "always present regardless of active workspace" (`DESIGN.md:226`) and to mirror the Resources-workspace toggle live (`EXPERIENCE.md` DevProxy toggle row and Do's/Don'ts: "Show DevProxy on/off as a persistent topology-bar chip across every workspace | Let fault injection stay silently on after leaving Resources").
8. **Was Medium — Whether Load Test's Run is gated by the confirmation modal was unstated.** Now resolved. `EXPERIENCE.md` Component Patterns, "Primary action button" row states explicitly that when a workspace's primary action is itself destructive (Load Test's Run, because Reset truncates the disposable stores as its first phase), "it renders and behaves as a Destructive action button instead... gated by the same Confirmation modal every time Run fires, including Journey B's repeated reruns." This is stated consistently in three places (Primary action button row, Destructive action button row, Confirmation modal row).
9. **Was Low — AppHost Switch didn't address orphaned in-flight state or stale evidence labeling.** Now resolved. `EXPERIENCE.md` State Patterns adds an "Evidence provenance across a topology switch" row: every evidence record carries its topology/profile and run-generation label; switching never deletes prior evidence but every pre-switch record stays visibly marked with its old topology/generation. Switch itself is confirmed to sit behind the same Confirmation modal as a plain Stop (Journey B step 5 narrates this explicitly).
10. **Was Low — "Ambiguous" copy was internally inconsistent.** Now resolved. Voice and Tone's canonical line reads "Ambiguous — not yet reconciled; Resend is unsafe; a fresh submission or an outcome query is the only way forward" — the "could not be reconciled" vs. "can move it forward" tension is gone from the line the presenter actually reads aloud; the State Patterns row keeps the fuller nuance ("without implying the outcome is permanently unresolvable") as supporting prose, not as the spoken line.

## Open items (not defects)

None.

## What is already strong

- **Status vocabulary remains genuinely truthful and stage-safe** — exact HTTP codes, exact field names, no paraphrasing (Voice and Tone table).
- **Color is never the sole channel**; `state-failed`/`state-healthy` remain reserved for proven outcomes only.
- **The confirmation modal is fully specified**: double-border, names the exact target and command, opens with Cancel focused, traps focus entirely, single typed `Y` key, `Escape` cancels, focus returns to the opener on either outcome — closing the previously-underspecified "where does focus land" question.
- **Inverse-recovery-in-same-slot** remains a strong, explicit pattern (Restart renders in Stop's exact slot).
- **Resend-same-key stays correctly disabled (not merely discouraged)** after an ambiguous Omitted-key outcome.
- **Failure branches remain written into all three flows**, not hidden.
