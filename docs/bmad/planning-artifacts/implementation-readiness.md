---
title: CoreBankDemo Implementation Readiness
status: resolved
date: 2026-08-30
resolved: 2026-08-30
resolution: sprint-change-proposal-2026-08-30.md
---

# Implementation Readiness

**Verdict: FAIL**

The plan is not implementable as currently recorded. Run `bmad-correct-course` before refreshing `sprint-status.yaml`.

## Findings

### 1. Story 6.1 conflicts with the accepted locking architecture

Story 6.1 requires the Aspire application graph to start a Dapr `lockstore` component. Accepted ADR-011 and completed Story 6.2 require that component to be absent because partition locks now use renewable leases through the Aspire-managed Redis instance.

**Required correction:** Amend Story 6.1 to require Dapr only for pub/sub and Redis for distributed locking.

### 2. Active work violates recorded dependency order

The brief addendum records strict epic order E0 through E7, but `sprint-status.yaml` has Epics 4, 5, 6, and 7 in progress simultaneously. Story 7.4 is in progress even though its acceptance criteria reuse the accepted-load workflow owned by backlog Stories 7.1 through 7.3.

**Required correction:** Either restore the recorded sequence or explicitly revise the dependency model and split Story 7.4 so its independently completable work is distinguishable from acceptance-harness integration.

### 3. Story 8.2 has a stale decision inventory

Story 8.2 scopes the decision record refresh to ADR-008 through ADR-014 and refers to `A7/A9-tiering`. ADR-015 and ADR-016 now govern active work, while A9 is not a defined cruft ruling in `docs/bmad/constraints.md`.

**Required correction:** Update Story 8.2 to account for ADR-015 and ADR-016 and replace the undefined A9 reference with the intended decision identifier.

## Resolution

Use `bmad-correct-course` for these cross-cutting planning changes. Rerun `bmad-sprint-planning` after the epic document and tracking sequence agree.

## Resolution

The approved `sprint-change-proposal-2026-08-30.md` resolved all findings:

1. Story 6.1 now requires shared Aspire Redis locking and explicitly excludes the Dapr `lockstore`.
2. The brief now permits dependency-gated overlap, and Story 7.4 cannot complete before Stories 7.1–7.3 and live rehearsal evidence.
3. Story 8.2 and the architecture spine now cover ADR-008..ADR-016 and no longer reference undefined ruling A9.
