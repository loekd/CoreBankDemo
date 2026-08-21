---
title: 'Story 1.2: Test projects and rebuild solution filter'
type: 'chore'
created: '2026-08-21'
status: 'done'
baseline_commit: 'beb6f2b0f6caf83ecafc4f66ea7ce58d98c551ac'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/constraints.md'
  - '{project-root}/tests/Directory.Build.props'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The rebuild gate needs a home: no test projects exist and there is no solution filter to run gates against while unmigrated projects go red (FR-27; AD-10).

**Approach:** Scaffold the four test projects under `tests/`, add them to the solution, and create `CoreBankDemo.Rebuild.slnf` covering ServiceDefaults, Messaging, and the test projects.

## Boundaries & Constraints

**Always:** Test project names end in `.Tests` (the shared props conditions on that); each test csproj sets coverlet `Include` filters scoped to its target assembly; xunit.v3 conventions; one passing smoke test per project.

**Ask First:** Changing production project files beyond adding solution entries.

**Never:** Add versions to csproj files; reference production packages the target project doesn't need; start rebuilding Messaging/ServiceDefaults sources (their epics do that).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Gate run | `dotnet test CoreBankDemo.Rebuild.slnf` | All 4 test projects run their smoke test; coverage gate active per project | N/A |
| Full solution | `dotnet build CoreBankDemo.sln` | Still green (old code untouched) | N/A |

</frozen-after-approval>

## Code Map

- `tests/Directory.Build.props` — shared gate config (story 1.1); conditions on project name ending `Tests`; test csprojs must set `<Include>[TargetAssembly]*</Include>` for coverlet
- `CoreBankDemo.sln` — add the 4 test projects (dotnet sln add)
- `CoreBankDemo.Messaging/CoreBankDemo.Messaging.csproj`, `CoreBankDemo.ServiceDefaults/…csproj`, `CoreBankDemo.CoreBankAPI/…csproj`, `CoreBankDemo.PaymentsAPI/…csproj` — reference targets for the test projects
- New: `tests/CoreBankDemo.Messaging.Tests/`, `tests/CoreBankDemo.ServiceDefaults.Tests/`, `tests/CoreBankDemo.CoreBankAPI.Tests/`, `tests/CoreBankDemo.PaymentsAPI.Tests/` — one csproj + one smoke test file each
- New: `CoreBankDemo.Rebuild.slnf` — filters CoreBankDemo.sln to ServiceDefaults, Messaging + the 4 test projects

## Tasks & Acceptance

**Execution:**
- [x] `tests/CoreBankDemo.{Messaging,ServiceDefaults,CoreBankAPI,PaymentsAPI}.Tests/*.csproj` — create (net10.0, ProjectReference to target, coverlet `Include` filter for its target assembly only) — minimal csprojs; shared props does the rest
- [x] `tests/*/SmokeTests.cs` — one `[Fact]` per project asserting a trivially true statement via AwesomeAssertions — proves runner + assertions wired
- [x] `CoreBankDemo.sln` — add the 4 projects — keeps IDE experience whole
- [x] `CoreBankDemo.Rebuild.slnf` — create with the 6-project set — the strangler gate (AD-10)

**Acceptance Criteria:**
- Given the repo, when `dotnet test CoreBankDemo.Rebuild.slnf` runs, then all four smoke tests pass and coverlet reports per-project coverage (smoke tests → no threshold failure since no covered assembly lines yet or filters scope to target)
- Given the full solution, when `dotnet build CoreBankDemo.sln` runs, then it is green

## Design Notes

Coverage note: with `Include=[Target]*` and zero tests touching the target, coverlet reports 0% but only for instrumented assemblies that are loaded. If the smoke-test-only state trips the 90% threshold, set `<Threshold>` override to 0 in each test csproj with a `TODO(story-N)` comment, removed by the first real test story of that project's epic — the gate must be honest by the time real code lands (epic stories flip it to 90).

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — expected: 4/4 projects pass
- `dotnet build CoreBankDemo.sln` — expected: green

## Spec Change Log (amendments)

- 2026-08-21 (step-04, bad_spec): Code Map wrongly mandated ProjectReferences from CoreBankAPI.Tests/PaymentsAPI.Tests to their unmigrated targets — MSBuild builds ProjectReferences transitively regardless of slnf membership, so the gate would break the moment those APIs go red (defeats AD-10). Amended: API test projects carry NO ProjectReference/Include until their epics (4/5) add them when the target enters the filter; smoke tests stay standalone. KEEP: minimal csprojs, per-target Include filters where a reference exists, Threshold=0 TODO tripwires. Applied as targeted fix (not full re-derive; re-derivation would be byte-identical otherwise).
- 2026-08-21 (step-04 close): patches applied; AD-10 empirically proven (gate exit 0 with #error in PaymentsAPI). Deviation kept: Threshold=0 stays on API test projects (zero instrumented modules otherwise fails inherited 90 gate); removal tied to stories 4.1/5.1.
