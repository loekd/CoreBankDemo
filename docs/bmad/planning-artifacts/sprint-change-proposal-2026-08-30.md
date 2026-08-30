---
title: CoreBankDemo Sprint Change Proposal
status: approved
date: 2026-08-30
trigger: implementation-readiness-fail
scope: moderate
mode: batch
---

# Sprint Change Proposal

## 1. Issue Summary

The 2026-08-30 implementation-readiness gate found three conflicts in the forward-looking plan:

1. Story 6.1 still requires a Dapr `lockstore`, although accepted ADR-011 and completed Story 6.2 replaced Dapr locking with renewable leases through Aspire-managed Redis.
2. The brief addendum declares strict E0-to-E7 execution, while valid work is active across Epics 4–7. Story 7.4 can develop against stable ports and fakes, but its live accepted-load integration and final rehearsal depend on backlog Stories 7.1–7.3.
3. Story 8.2 treats ADR-008..ADR-014 as unwritten future work and references undefined ruling `A9`, although ADR-008..ADR-016 are accepted and ADR-012 is partially superseded by ADR-016.

This is planning drift, not an implementation failure. The accepted architecture and completed Redis/PostgreSQL work remain valid.

## 2. Impact Analysis

### Epic impact

- **Epic 6:** remains viable. Story 6.1 needs one acceptance-criterion correction so its AppHost graph agrees with completed Story 6.2 and ADR-011.
- **Epic 7:** remains viable. Story 7.4 may continue in parallel, but its completion gate must explicitly wait for Stories 7.1–7.3. No new story is required.
- **Epic 8:** remains viable. Story 8.2 must become an audit/alignment story for the accepted ADR set rather than a request to create already-written ADRs.
- **Epics 1–5:** no scope change. Completed stories retain their historical wording and statuses.

### Story impact

- Story 6.1: correct the Aspire resource inventory.
- Story 7.4: distinguish independently testable implementation from dependency-gated live integration and rehearsal.
- Story 8.2: correct the decision inventory and supersession scope.
- No story is added, removed, renumbered, downgraded, or rolled back.

### Artifact conflicts

- **Product brief addendum:** replace strict serial execution with an explicit dependency-gated sequencing policy.
- **Product brief:** clarify that bottom-up order defines dependency priority, not a ban on safe overlap.
- **Architecture spine:** include ADR-016 in the source and accepted-decision inventories.
- **PRD:** no change. FR-1..FR-29, NFR-1..NFR-5, scope, and MVP remain achievable.
- **UX:** no separate UX artifact exists; Story 7.4's approved UX blueprint is unchanged.
- **Readiness report:** mark the failed findings resolved only after the approved edits are applied.

### Technical impact

No production code, API contract, schema, package, infrastructure resource, or test behavior changes. The proposal aligns planning with implementation already governed by ADR-011, ADR-015, and ADR-016.

## 3. Recommended Approach

**Approach: Direct adjustment with dependency gates.**

- Preserve all completed and in-progress work.
- Permit overlap when a story can proceed against stable contracts, ports, and fakes.
- Prevent premature completion: Story 7.4 remains `in-progress` until Stories 7.1–7.3 provide the live accepted-load workflow and the real rehearsal passes.
- Correct stale story and architecture inventories.
- Rerun `bmad-sprint-planning` after the edits; let its deterministic script refresh tracking without status downgrades.

**Effort:** Low.  
**Risk:** Low.  
**Timeline impact:** None beyond making Story 7.4's existing upstream wait explicit.  
**Rollback:** Not viable or useful; it would discard valid completed Redis, PostgreSQL, and DemoRunner work.  
**MVP review:** Not required; product scope and success criteria are unchanged.

## 4. Detailed Change Proposals

### Product brief — Approach

**Current:**

> Bottom-up epic order (test infra → Messaging → ServiceDefaults → CoreBankAPI → PaymentsAPI → AppHost → load-test realignment → docs), gated per story by `dotnet test` on a solution filter (`CoreBankDemo.Rebuild.slnf`) so the gate is always green even while unmigrated projects are red.

**Proposed:**

> Bottom-up dependency order (test infra → Messaging → ServiceDefaults → CoreBankAPI/PaymentsAPI → AppHost → load-test realignment → docs), gated per story by `dotnet test` on `CoreBankDemo.Rebuild.slnf`. Stories may overlap when their recorded prerequisites and stable contracts are available; overlap never permits a story to claim live integration or completion before its dependency gate passes.

**Rationale:** Preserve the intended architectural sequence while accurately describing safe parallel work already in progress.

### Product brief addendum — Epic sequencing

**Current:**

> Strict dependency order E0 → E1 → … → E7. ~30 stories total. Stories sized ≤ one class-cluster (agent context guardrail).

**Proposed:**

> Dependency spine: E0 → E1 → E2 establishes the shared test, messaging, and service foundation. E3 and E4 may overlap once their shared prerequisites exist. E5 work may overlap when the affected service seams are stable. E6 implementation may proceed behind stable ports and fakes, but live load integration and acceptance cannot complete until the required E3–E5 stories are done. E7 completes after the implementation and acceptance evidence it documents. Advanced statuses are preserved; a story remains in progress while any recorded completion dependency is unmet. Stories remain sized to an agent-safe class cluster unless a human-approved story explicitly records a broader boundary.

**Rationale:** Replace an obsolete global serialization rule with explicit, enforceable dependency gates.

### Story 6.1 — Aspire application graph acceptance criteria

**Current:**

> Then Postgres (paymentsdb, corebankdb, pgAdmin), Redis (+ RedisInsight), Jaeger, Dapr components (pubsub, lockstore, subscription), and both APIs with sidecars come up healthy

**Proposed:**

> Then Postgres (paymentsdb, corebankdb, pgAdmin), Redis (+ RedisInsight), Jaeger, Dapr pub/sub and subscription components, and both APIs with sidecars come up healthy; both APIs receive the shared Aspire Redis connection for distributed locking, and no Dapr `lockstore` component exists

**Rationale:** Align the pending AppHost story with accepted ADR-011 and completed Story 6.2.

### Story 7.4 — Dependency-gated completion

**Current:**

> Given any action is running, failed, cancelled, or ambiguous  
> When the speaker tries to advance  
> Then Next remains unavailable...

The story does not state what may proceed before Stories 7.1–7.3 or what prevents premature completion.

**Proposed addition:**

> **Given** Stories 7.1–7.3 are not yet done  
> **When** Story 7.4 implementation proceeds  
> **Then** the scenario model, state machine, process ownership, allow-listed adapters, TUI, and load-workflow presentation contract may be implemented and tested through ports and fakes  
> **And** live LoadTestSupport binding, a successful five-invariant rehearsal proof pack, and Story 7.4 completion remain blocked until Stories 7.1–7.3 are done  
> **When** Story 7.3 establishes the accepted load workflow  
> **Then** Story 7.4 binds to those exact endpoints and evidence semantics, completes the live rehearsal and presentation-terminal dress rehearsal, and introduces no parallel assertion path.

**Rationale:** Preserve substantial valid DemoRunner work while making its real completion dependency explicit.

### Story 7.4 implementation spec — Task wording

**Current:**

> [x] Reuse Story 7.3's accepted load-test sequence and evidence sources for Run → Wait → Assert → Investigate; do not create parallel reset, drain, invariant, or trace semantics for the TUI.

**Proposed:**

> [x] Implement the Run → Wait → Assert → Investigate presentation contract behind `ILoadWorkflowRunner`, using the endpoint and five-invariant semantics already frozen by the PRD, architecture, and Story 7.3 acceptance criteria; do not create a parallel assertion path.  
> [ ] After Stories 7.1–7.3 are done, bind the runner to their accepted live endpoints and evidence sources, produce a successful five-invariant rehearsal proof pack, and complete the presentation-terminal dress rehearsal.

**Proposed change-log addition:**

> 2026-08-30: Course correction approved parallel implementation behind stable ports and fakes, but explicitly gates live LoadTestSupport binding, successful proof-pack rehearsal, and Story 7.4 completion on Stories 7.1–7.3.

**Rationale:** The current checked task overstates integration with a story that is still backlog. The revised wording records what is genuinely complete without changing the human-approved intent or UX.

### Story 8.2 — ADR and skill alignment

**Current user story:**

> As the process record, I want the rulings written as ADRs, so that decisions outlive the chat (rulings A1–A4, A7/A9-tiering, contract generation, replicated local topology; NFR-4).

**Proposed user story:**

> As the process record, I want the accepted rebuild decisions audited against the final code and documentation, so that ADR-008..ADR-016, their supersession links, and the project skills describe the implemented system accurately (rulings A1–A4 and A7–A8; NFR-4).

**Current acceptance criteria:**

> When ADR-008..ADR-014 are written (...)  
> Then each follows the existing ADR format with Context/Decision/Implementation references to real files

**Proposed acceptance criteria:**

> **Given** ADR-008..ADR-016 are accepted  
> **When** the final documentation audit runs  
> **Then** each ADR's status, Context, Decision, Consequences, supersession links, and implementation references match the final code; ADR-012 identifies ADR-016's PostgreSQL Testcontainers supersession, ADR-015 records the presentation-tool exception, and no forward-looking artifact refers to an undefined ruling  
> **And** `.claude/skills` (`conventions`, `messaging-patterns`, `observability`) are updated where implemented surfaces changed; `ARCHITECTURE.md` links the complete ADR set; and the `AGENTS.md` rebuild section flips to "completed" only after the implementation and acceptance gates pass.

**Rationale:** Convert stale creation language into the final evidence-based audit the documentation epic actually needs.

### Architecture spine — ADR inventory

**Current:**

> sources: ... `docs/adr/ADR-001..015`

> ADR-008..ADR-015 are accepted; their remaining code and orchestration work is owned by the corresponding stories.

**Proposed:**

> sources: ... `docs/adr/ADR-001..016`

> ADR-008..ADR-016 are accepted; ADR-016 partially supersedes ADR-012's persistence-tier engine while preserving its three-tier model and coverage gate. Remaining code, orchestration, acceptance, and documentation alignment is owned by the corresponding stories.

**Rationale:** Make the architecture inventory include the accepted PostgreSQL persistence-testing decision.

### Readiness report — Resolution marker

After all approved edits are applied:

**Current:**

> status: fail

**Proposed:**

> status: resolved
> resolved: 2026-08-30
> resolution: sprint-change-proposal-2026-08-30.md

Append a resolution note mapping each finding to the changed artifact. Do not erase the original evidence.

**Rationale:** Preserve the gate result as history while preventing a resolved report from masquerading as a current blocker.

## 5. Implementation Handoff

**Scope classification:** Moderate planning correction. No production implementation or fundamental replan is required, but coordinated edits span the brief, active backlog, architecture, and an approved in-progress story spec.

**Recipients:**

- **Product Owner / Developer:** apply the approved artifact edits exactly as proposed.
- **Developer:** preserve current story statuses; do not rewrite completed historical stories; keep Story 7.4 `in-progress`.
- **Sprint planning workflow:** rerun readiness and deterministic tracking refresh after edits.

**Success criteria:**

1. Story 6.1 contains no Dapr `lockstore` requirement and explicitly uses shared Redis for locks.
2. The brief records dependency-gated overlap instead of strict global serialization.
3. Story 7.4 can proceed behind ports/fakes but cannot complete before Stories 7.1–7.3 and live rehearsal evidence.
4. Story 8.2 and the architecture spine account for ADR-008..ADR-016 and contain no `A9` reference.
5. No completed or advanced sprint status is downgraded.
6. A rerun of `bmad-sprint-planning` passes readiness and refreshes `sprint-status.yaml` without orphaned entries.

## Checklist Record

| Item | Status | Finding |
|---|---|---|
| 1.1–1.3 Trigger and evidence | [x] | Readiness gate and accepted ADR/story evidence identify planning drift. |
| 2.1–2.5 Epic impact | [x] | Epics remain viable; direct edits and dependency clarification suffice. |
| 3.1 PRD | [x] | No requirement or MVP change. |
| 3.2 Architecture | [!] | ADR inventory must include ADR-016. |
| 3.3 UI/UX | [N/A] | No separate UX artifact; approved Story 7.4 UX is unchanged. |
| 3.4 Other artifacts | [!] | Brief, Story 7.4 spec, and readiness report require alignment. |
| 4.1 Direct adjustment | [x] Viable | Low effort and risk; preserves momentum. |
| 4.2 Rollback | [x] Not viable | No implementation defect warrants reverting valid work. |
| 4.3 MVP review | [x] Not viable | Scope and goals remain achievable unchanged. |
| 4.4 Recommended path | [x] | Direct adjustment with explicit dependency gates. |
| 5.1–5.5 Proposal components | [x] | Included above. |
| 6.1–6.2 Review | [x] | Proposal is internally consistent and actionable. |
| 6.3 User approval | [x] | Approved by Loek on 2026-08-30 without conditions. |
| 6.4 Sprint status update | [N/A] | No entries change; deterministic refresh follows approved edits. |
| 6.5 Handoff | [x] | Routed to Product Owner / Developer for artifact edits and sprint-planning refresh. |

## Approval and Handoff Log

- **Approved by:** Loek
- **Date:** 2026-08-30
- **Conditions:** None
- **Implemented artifacts:** product brief, brief addendum, epic plan, architecture spine, Story 7.4 implementation spec, and readiness report
- **Sprint tracking:** no status entries added, removed, renumbered, or downgraded
- **Next route:** rerun `bmad-sprint-planning` to verify readiness and refresh deterministic tracking
