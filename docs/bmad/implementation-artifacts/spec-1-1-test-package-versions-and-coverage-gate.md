---
title: 'Story 1.1: Test package versions and coverage gate'
type: 'chore'
created: '2026-08-21'
status: 'done'
baseline_commit: 'a28b674414c3ae63aef03d959b7503252e53fd06'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/constraints.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The rebuild needs a coverage gate that exists before any production code: no test packages are pinned centrally and nothing enforces the ≥90% line threshold (FR-27, FR-28; AD-9).

**Approach:** Pin the full test stack in `Directory.Packages.props` and create `tests/Directory.Build.props` so every test project under `tests/` inherits coverlet enforcement from plain `dotnet test` (VSTest mode, no MTP).

## Boundaries & Constraints

**Always:** Central package management only (no versions in csproj files); coverlet.msbuild `Threshold=90`, `ThresholdType=line`; exclude by attribute `ExcludeFromCodeCoverage`; VSTest runner mode.

**Ask First:** Any change to production package versions.

**Never:** Enable Microsoft.Testing.Platform (coverlet incompatible — the gate would silently vanish); add test packages outside `Directory.Packages.props`; touch production projects in this story.

</frozen-after-approval>

## Code Map

- `Directory.Packages.props` — central package versions; add test package pins here (keep existing production pins untouched)
- `tests/` — does not exist yet; this story creates only `tests/Directory.Build.props` (projects arrive in story 1.2)
- Versions verified on NuGet 2026-08-21 (spine Stack table): xunit.v3 4.0.0, xunit.runner.visualstudio 4.0.0, Microsoft.NET.Test.Sdk 18.9.0, AwesomeAssertions 9.6.0, Moq 4.20.72, coverlet.collector 10.0.1, coverlet.msbuild 10.0.1, Microsoft.EntityFrameworkCore.Sqlite 10.0.8

## Tasks & Acceptance

**Execution:**
- [x] `Directory.Packages.props` — add the eight test package pins listed in Code Map — central pinning per convention
- [x] `tests/Directory.Build.props` — create with: `CollectCoverage=true`, `Threshold=90`, `ThresholdType=line`, `ExcludeByAttribute=ExcludeFromCodeCoverage`, `Exclude=[*]*.Program`, `IsPackable=false`, common `ItemGroup` adding xunit.v3, runner, Test.Sdk, AwesomeAssertions, Moq, coverlet.msbuild + coverlet.collector so test csprojs stay minimal

**Acceptance Criteria:**
- Given the repo root, when `dotnet restore` runs on a scratch test project under `tests/`, then all eight packages resolve at the pinned versions without csproj-level versions
- Given `tests/Directory.Build.props`, when a test project under `tests/` runs `dotnet test`, then coverlet msbuild enforcement is active (threshold visible in output) under VSTest — no MTP opt-in exists anywhere in the repo

## Verification

**Commands:**
- `dotnet build CoreBankDemo.Messaging/CoreBankDemo.Messaging.csproj` — expected: existing solution unaffected, builds green
- `grep -r "TestingPlatform" --include="*.props" --include="*.csproj" --include="global.json" .` — expected: exactly two matches in `tests/Directory.Build.props`: `IsTestingPlatformApplication=false` and `UseMicrosoftTestingPlatformRunner=false` (xunit.v3 4.0 defaults MTP **on**; this is the disable switch that keeps VSTest+coverlet per AD-9)

## Spec Change Log

- 2026-08-21 (step-03): xunit.v3 4.0.0 enables Microsoft.Testing.Platform by default and MTP 2.3.3 hard-errors the VSTest target on .NET 10 SDK. Amended verification to whitelist the single `IsTestingPlatformApplication=false` disable in `tests/Directory.Build.props` — the only way to satisfy exact pins + VSTest + coverlet gate simultaneously. KEEP: versionless shared ItemGroup in Directory.Build.props so test csprojs stay minimal.
- 2026-08-21 (step-04): review patches applied: [*]Program global-namespace exclude, guarded parent import, .Tests name condition, collector made opt-in, MTP double-disable + explicit ThresholdStat=minimum, trailing newline. Gate re-proven fail@50%/pass@100% under VSTest with name guard active. KEEP: per-test-project <Include> filter requirement (story 1.2).
