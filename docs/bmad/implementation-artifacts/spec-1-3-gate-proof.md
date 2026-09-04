---
title: 'Story 1.3: Gate proof'
type: 'chore'
created: '2026-08-21'
status: 'done'
baseline_commit: 'ac146d1f9d789d2fd3a32e4e17111928b4955222'
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
- [x] Temp canary run — capture failing output (exit code + threshold error line) into Design Notes
- [x] Revert canary — capture passing `dotnet test CoreBankDemo.Rebuild.slnf` summary into Design Notes
- [x] `tests/CoreBankDemo.Messaging.Tests/GateProofTests.cs` — add permanent filter-vs-assembly guard test

**Acceptance Criteria:**
- Given the Design Notes, when read, then both verbatim outcomes (fail + pass) are present with dates
- Given `git status` at story end, when inspected, then only GateProofTests.cs and this spec differ from the story baseline

## Design Notes

### Gate proof run 1 — canary in → FAIL (2026-08-21)

Temporary state: `tests/CoreBankDemo.Messaging.Tests/CoreBankDemo.Messaging.Tests.csproj` override flipped `<Threshold>0</Threshold>` → `<Threshold>90</Threshold>`; temp canary class `CoreBankDemo.Messaging/CanaryGateProof.cs` (one covered method, one uncovered) plus temp `tests/CoreBankDemo.Messaging.Tests/CanaryTests.cs` covering only the covered path.

Command: `dotnet test tests/CoreBankDemo.Messaging.Tests/CoreBankDemo.Messaging.Tests.csproj` — **exit code 1**. Verbatim tail of output:

```text
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 15 ms - CoreBankDemo.Messaging.Tests.dll (net10.0)
  [coverlet]
  Calculating coverage result...
   Generating report '/Users/loekd/projects/CoreBankDemo/tests/CoreBankDemo.Messaging.Tests/coverage.json'

+------------------------+-------+--------+--------+
| Module                 | Line  | Branch | Method |
+------------------------+-------+--------+--------+
| CoreBankDemo.Messaging | 0.85% | 0%     | 2.5%   |
+------------------------+-------+--------+--------+

+---------+-------+--------+--------+
|         | Line  | Branch | Method |
+---------+-------+--------+--------+
| Total   | 0.85% | 0%     | 2.5%   |
+---------+-------+--------+--------+
| Average | 0.85% | 0%     | 2.5%   |
+---------+-------+--------+--------+

/Users/loekd/.nuget/packages/coverlet.msbuild/10.0.1/buildMultiTargeting/coverlet.msbuild.targets(73,5): error : The minimum line coverage is below the specified 90 [/Users/loekd/projects/CoreBankDemo/tests/CoreBankDemo.Messaging.Tests/CoreBankDemo.Messaging.Tests.csproj]
/Users/loekd/.nuget/packages/coverlet.msbuild/10.0.1/buildMultiTargeting/coverlet.msbuild.targets(73,5): error :  [/Users/loekd/projects/CoreBankDemo/tests/CoreBankDemo.Messaging.Tests/CoreBankDemo.Messaging.Tests.csproj]
/Users/loekd/.nuget/packages/coverlet.msbuild/10.0.1/buildMultiTargeting/coverlet.msbuild.targets(73,5): error :    at Coverlet.MSbuild.Tasks.CoverageResultTask.Execute() in /_/src/legacy/coverlet.msbuild.tasks/CoverageResultTask.cs:line 216 [/Users/loekd/projects/CoreBankDemo/tests/CoreBankDemo.Messaging.Tests/CoreBankDemo.Messaging.Tests.csproj]
EXIT_CODE=1
```

Test execution itself passed (2/2); the build failed on the coverlet threshold check, proving the 90% line gate fails the build.

### Gate proof run 2 — canary out → PASS (2026-08-21)

Canary class + canary test deleted, csproj override restored to `<Threshold>0</Threshold>` via `git checkout`; permanent `GateProofTests.cs` added.

Command: `dotnet test CoreBankDemo.Rebuild.slnf` — **exit code 0**. Verbatim per-project summary lines:

```text
Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 18 ms - CoreBankDemo.PaymentsAPI.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 15 ms - CoreBankDemo.CoreBankAPI.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 15 ms - CoreBankDemo.ServiceDefaults.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 14 ms - CoreBankDemo.Messaging.Tests.dll (net10.0)
EXIT_CODE=0
```

5 tests total across the 4 test projects; Messaging.Tests now runs 2 (SmokeTests + the new GateProofTests).

### Vacuous-filter guard negative check (2026-08-21)

To confirm the permanent guard is not itself vacuous, the csproj `Include` was temporarily typo'd to `[CoreBankDemo.Messagng]*` and `dotnet test --filter GateProofTests` run — the guard failed as intended, then the csproj was restored:

```text
   Expected includeFilter to be the same string because a filter that does not name the target assembly makes the 90% gate pass vacuously, but they differ at index 20:
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 32 ms - CoreBankDemo.Messaging.Tests.dll (net10.0)
```

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — expected: green, 5+ tests incl. GateProofTests
- `git status --short` — expected: no canary residue
