---
title: 'Story 2.1: Identity, constants, and message contracts'
type: 'feature'
created: '2026-08-21'
status: 'done'
baseline_commit: 'b8cea0875050e386f56307ec32761a18ba3e8d3d'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-2-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The kernel rebuild starts here: partition assignment, statuses, and message contracts must exist once, test-first, before any processor code (FR-4, FR-19; AD-4). The old Messaging sources must be demolished at epic start (AD-10).

**Approach:** `git rm` all old `CoreBankDemo.Messaging/*.cs`, then TDD-rebuild `PartitionHelper` (FNV-1a, behavior-identical to legacy), `MessageConstants` (verbatim legacy values), and the `IMessage`/`IInboxMessage`/`IOutboxMessage` contracts per the epic context. Remove the Threshold=0 override from Messaging.Tests (epic-1 carry-forward) — the 90% gate goes live for the kernel.

## Boundaries & Constraints

**Always:** Tests written before implementation (red → green); FNV-1a produces identical partition ids to legacy for identical inputs (known-vector tests from the legacy constants: prime 16777619, offset basis 2166136261, char-based, `Math.Abs % count`); MessageConstants values verbatim from the epic context's legacy reference; contracts follow the epic context member lists with AD-4/AD-11 adjustments noted there.

**Ask First:** Any deviation from legacy partition math (would break ordering compatibility).

**Never:** Re-add legacy AD violations (check-then-insert, Postgres-only violation catch, missing terminal Failed); start processor/repository code (stories 2.2+); touch other projects' sources.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Determinism | Same key, repeated calls, any casing preserved | Same partition id | N/A |
| Range | Any string key, partitionCount=4 | id in [0,4) | N/A |
| Known vectors | Legacy-computed key→id pairs | Identical ids | N/A |
| Degenerate keys | "", very long, unicode | Deterministic id in range, no throw | N/A |
| Invalid count | partitionCount <= 0 | ArgumentOutOfRangeException | thrown |

</frozen-after-approval>

## Code Map

- `docs/bmad/implementation-artifacts/epic-2-context.md` — Legacy Behavioral Reference section: exact algorithm, verbatim constants, contract member lists, AD-violation flags (do not copy violations)
- `CoreBankDemo.Messaging/` — demolish all `.cs` (git rm), keep csproj; new sources: `PartitionHelper.cs`, `MessageConstants.cs`, `IMessage.cs`/`IInboxMessage.cs`/`IOutboxMessage.cs`
- `tests/CoreBankDemo.Messaging.Tests/` — new test files per class; existing SmokeTests/GateProofTests reference `MessageConstants` — must compile at story end
- `tests/CoreBankDemo.Messaging.Tests/CoreBankDemo.Messaging.Tests.csproj` — DELETE the `<Threshold>0</Threshold>` override + TODO comment (gate live at 90)

## Tasks & Acceptance

**Execution:**
- [x] `git rm CoreBankDemo.Messaging/*.cs` (all legacy sources) — epic demolition moment (AD-10)
- [x] `tests/.../PartitionHelperTests.cs` then `CoreBankDemo.Messaging/PartitionHelper.cs` — TDD, matrix rows covered incl. legacy known vectors
- [x] `tests/.../MessageConstantsTests.cs` then `MessageConstants.cs` — verbatim values pinned by test
- [x] `IMessage.cs`, `IInboxMessage.cs`, `IOutboxMessage.cs` — contracts per epic context (id, dedupe identity, PartitionId, Status, RetryCount, timestamps, TraceParent/TraceState, LastError)
- [x] Messaging.Tests csproj — remove Threshold=0 override

**Acceptance Criteria:**
- Given the rebuilt kernel, when `dotnet test CoreBankDemo.Rebuild.slnf` runs, then all tests pass AND the Messaging 90% line gate is enforced and met
- Given legacy known key→partition vectors, when replayed against the new PartitionHelper, then ids match exactly
- Given the kernel project, when inspected, then no status/limit literal exists outside MessageConstants

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — expected: green, Messaging coverage ≥90 enforced (no Threshold override in csproj)
- `git status --short CoreBankDemo.Messaging/` — expected: only deletions of legacy files + new listed sources

## Spec Change Log

- 2026-08-21 (step-04): review patches: int.MinValue hash mapped to partition 0 via internal MapHashToPartition seam (Math.Abs overflow repair; legacy crashed on such keys so no row depends on other mapping); null-key ArgumentNullException documented as sanctioned refinement; IMessage.PartitionId doc fixed; status-distinctness test added. Vector-count note: 12 pinned vectors + the empty-string legacy-throw observation were misreported as "13 vectors" in the 2.1 commit message. Provenance: vectors captured by executing the legacy PartitionHelper via throwaway harness at partitionCount 4 pre-demolition (keys listed in PartitionHelperTests TheoryData). Rejected: ADR path staleness (story 8.2 owns), AD-4 store-dedupe marker (story 2.2 owns), interface-shape guard (over-testing).
