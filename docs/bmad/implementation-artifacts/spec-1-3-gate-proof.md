---
title: 'Story 1.3: Gate proof'
type: 'chore'
created: '2026-08-21'
status: 'draft'
review_loop_iteration: 0
context:
  - '{project-root}/tests/Directory.Build.props'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The 90% gate's enforcement has only been demonstrated on since-deleted scratch projects; nothing in the repo re-executably proves the threshold fails builds, and a typo'd coverlet `Include` filter would pass vacuously forever (FR-28).

**Approach:** Prove both gate outcomes on the real Messaging.Tests project (canary in → fail; canary out → pass), record both outputs in this spec, and leave a permanent self-check test that guards against vacuous filters.

## Boundaries & Constraints

**Always:** The canary is temporary — working tree must be clean of it at story end except the permanent self-check; both command outputs captured verbatim in Design Notes.

**Ask First:** Any permanent change to gate thresholds or filters.

**Never:** Leave the canary class, threshold edits, or scratch projects behind; weaken the gate to make the proof pass.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Canary in | Messaging.Tests Threshold=90 + partially-covered canary class in Messaging | `dotnet test` exit ≠ 0 with line-coverage threshold error | N/A |
| Canary out | Overrides restored, canary deleted | `dotnet test CoreBankDemo.Rebuild.slnf` exit 0 | N/A |
| Vacuous filter guard | Messaging.Tests runs | Permanent test asserts the referenced target assembly name equals the module named in the csproj `Include` filter | Fails if filter/assembly drift |

</frozen-after-approval>

## Code Map

- `tests/CoreBankDemo.Messaging.Tests/` — canary host: temporarily flip `<Threshold>0</Threshold>`→`90`, add temp canary class to `CoreBankDemo.Messaging` + partial-cover test; then revert
- `tests/CoreBankDemo.Messaging.Tests/GateProofTests.cs` — permanent: reads its own csproj `Include` filter value (embed as constant or assembly metadata) and asserts it matches `typeof(<MessagingType>).Assembly.GetName().Name` wrapped in `[CoreBankDemo.Messaging]*`

## Tasks & Acceptance

**Execution:**
- [ ] Temp canary run — capture failing output (exit code + threshold error line) into Design Notes
- [ ] Revert canary — capture passing `dotnet test CoreBankDemo.Rebuild.slnf` summary into Design Notes
- [ ] `tests/CoreBankDemo.Messaging.Tests/GateProofTests.cs` — add permanent filter-vs-assembly guard test

**Acceptance Criteria:**
- Given the Design Notes, when read, then both verbatim outcomes (fail + pass) are present with dates
- Given `git status` at story end, when inspected, then only GateProofTests.cs and this spec differ from the story baseline

## Design Notes

(Outputs captured during execution land here.)

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — expected: green, 5+ tests incl. GateProofTests
- `git status --short` — expected: no canary residue
