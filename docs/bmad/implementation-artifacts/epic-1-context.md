# Epic 1 Context: E0 — Test Infrastructure & Scaffolding

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Stand up the test infrastructure and rebuild gate before any production code is rebuilt: pinned test packages, four scaffolded test projects, a coverage gate enforced by plain `dotnet test`, and the `CoreBankDemo.Rebuild.slnf` solution filter — plus proof that the gate actually fails a build below threshold. Every later epic inherits this gate; the rebuild is story-driven and TDD-first, so the gate must exist and be demonstrably trustworthy before story one of the messaging kernel.

## Stories

- Story 1.1: Test package versions and coverage gate
- Story 1.2: Test projects and rebuild solution filter
- Story 1.3: Gate proof

## Requirements & Constraints

- Every logic project gets an xUnit test project using AwesomeAssertions and Moq; tests are written test-first per story.
- Plain `dotnet test` on the rebuild solution filter must enforce ≥90% line coverage on logic projects via coverlet — locally, no CI required. Falling below the threshold must fail the run.
- Hosting boilerplate is excluded from coverage: `Program.cs` wiring, AppHost assemblies, generated code — via `[ExcludeFromCodeCoverage]` attributes plus coverlet filters (never blanket class exclusions for logic code).
- The gate is a first-class deliverable: no meaningless assertion-free tests; test names should state the contract being proven; xUnit `[Fact]`/`[Theory]` with AwesomeAssertions `Should()` syntax is the house style.
- Success criterion for the epic: the gate demonstrably fails when coverage drops (canary test) and passes when it doesn't — both outcomes captured in the story record.

## Technical Decisions

- **Test tiers (test architecture decision):** (1) pure logic tested with Moq against ports; (2) repository/store behavior tested on EF Core SQLite in-memory; (3) Postgres-only semantics (e.g. `FOR UPDATE`) covered solely by the k6/Postgres acceptance tier. This epic only scaffolds tier 1/2 infrastructure — SQLite package is pinned now so later epics can use it.
- **VSTest runner mode is mandatory.** Microsoft.Testing.Platform must not be enabled anywhere (no MTP opt-in in props or project files): coverlet is incompatible with MTP and the coverage gate would silently vanish.
- **Pinned versions** (central package management via `Directory.Packages.props`; versions verified current as of planning date): xunit.v3 4.0.0, xunit.runner.visualstudio 4.0.0, Microsoft.NET.Test.Sdk 18.9.0, AwesomeAssertions 9.6.0, Moq 4.20.72, coverlet.collector + coverlet.msbuild 10.0.1, Microsoft.EntityFrameworkCore.Sqlite 10.0.8 (pinned to the EF Core family in use). Existing production pins intentionally lag latest — do not upgrade them.
- **Coverage gate location:** `tests/Directory.Build.props` sets `CollectCoverage=true`, `Threshold=90`, `ThresholdType=line`, excludes `[*]*.Program` and AppHost assemblies, and `ExcludeByAttribute=ExcludeFromCodeCoverage` — so every test project under `tests/` inherits the gate with zero per-project setup.
- **Strangler gate (solution filter):** all story/epic gates for the rebuild run against `CoreBankDemo.Rebuild.slnf` at the repo root. Projects enter the filter at the start of their own epic (old sources deleted, tests added first). The full `.sln` only needs to be green again once the AppHost epic completes.
- **Layout:** four test projects at `tests/CoreBankDemo.Messaging.Tests`, `tests/CoreBankDemo.ServiceDefaults.Tests`, `tests/CoreBankDemo.CoreBankAPI.Tests`, `tests/CoreBankDemo.PaymentsAPI.Tests`, each referencing its target project and the pinned packages, each with one passing smoke test.
- **Filter contents at end of this epic:** ServiceDefaults, Messaging, and the four test projects. Note: Messaging and ServiceDefaults enter the filter now, but their rebuild (and demolition of their old sources) starts in their own epics — old sources stay until then, so the filter must build green against the existing code.
- This is a brownfield rebuild inside an existing solution — no starter template; work within the existing repo/solution structure and central package management.

## Cross-Story Dependencies

- 1.1 → 1.2: the test projects reference the centrally pinned packages and inherit the `tests/Directory.Build.props` gate.
- 1.2 → 1.3: gate proof runs `dotnet test CoreBankDemo.Rebuild.slnf`, which needs the filter and at least one filtered test project to exist.
- All later epics depend on this epic: strict order E0 → E1 → … — every subsequent story's gate is `dotnet test CoreBankDemo.Rebuild.slnf` with the ≥90% threshold established here.
